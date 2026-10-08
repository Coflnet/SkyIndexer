#!/usr/bin/env python3
"""Private, read-only player exports and offline verification (standard library)."""
import argparse
import base64
import csv
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import stat
import subprocess
import sys
import tempfile
import urllib.request
import uuid
import zipfile

SCHEMA = 'coflnet-player-export/v1'
REQUIRED = ('format.json', 'identity.json', 'sql/player.jsonl', 'sql/auctions.jsonl',
            'sql/bids.jsonl', 'sql/auction-target-relations.jsonl')


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def canonical(value):
    if not isinstance(value, str):
        raise ValueError('UUID must be a string')
    return uuid.UUID(value).hex


def json_load(data):
    def pairs(items):
        result = {}
        keys = set()
        for key, value in items:
            if key.casefold() in keys:
                raise ValueError('Duplicate JSON field')
            keys.add(key.casefold())
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def rows(data):
    return [json_load(line) for line in data.decode().splitlines() if line.strip()]


def validate(files):
    for name in REQUIRED:
        if name not in files:
            raise ValueError('Missing required file: ' + name)
    fmt = json_load(files['format.json'])
    target = canonical(fmt['playerUuid'])
    if fmt['schema'] != SCHEMA or fmt['playerUuid'] != target:
        raise ValueError('Invalid export format or noncanonical UUID')
    timestamp = dt.datetime.fromisoformat(fmt['createdAtUtc'].replace('Z', '+00:00'))
    if timestamp.utcoffset() != dt.timedelta(0):
        raise ValueError('Assembly timestamp must be UTC')
    identity = json_load(files['identity.json'])
    if canonical(identity['minecraftUuid']) != target:
        raise ValueError('Identity does not match export target')
    players = rows(files['sql/player.jsonl'])
    if len(players) != 1 or type(players[0].get('Id')) is not int or players[0]['Id'] <= 0:
        raise ValueError('v1 export requires exactly one positive SQL player ID')
    ids = set()
    auction_map = {}
    auction_uuids = {}
    bid_ids = set()
    for p in players:
        if not {'Id', 'UuId', 'Name', 'UpdatedAt'} <= p.keys():
            raise ValueError('Player snapshot is missing required raw fields')
        if canonical(p['UuId']) != target or type(p['Id']) is not int:
            raise ValueError('Player ownership mismatch')
        ids.add(p['Id'])
    for a in rows(files['sql/auctions.jsonl']):
        if not {'Id', 'Uuid', 'SellerId', 'AuctioneerId', 'ProfileId', 'Start', 'End', 'HighestBidAmount', 'StartingBid', 'UId'} <= a.keys():
            raise ValueError('Auction snapshot is missing required raw fields')
        if type(a['SellerId']) is not int or a['SellerId'] not in ids or canonical(a['AuctioneerId']) != target:
            raise ValueError('Auction ownership mismatch')
        au = canonical(a['Uuid'])
        if type(a['Id']) is not int or a['Id'] <= 0 or a['Id'] in auction_map or au in auction_uuids:
            raise ValueError('Invalid or duplicate auction identity')
        auction_map[a['Id']] = au
        auction_uuids[au] = a['Id']
    for b in rows(files['sql/bids.jsonl']):
        r = b['record']
        if not {'Id', 'Uuid', 'BidderId', 'Bidder', 'ProfileId', 'Amount', 'Timestamp'} <= r.keys():
            raise ValueError('Bid snapshot is missing required raw fields')
        if type(r['BidderId']) is not int or r['BidderId'] not in ids or canonical(r['Bidder']) != target:
            raise ValueError('Bid ownership mismatch')
        au = canonical(b['auctionUuid'])
        if type(r.get('Id')) is not int or r['Id'] <= 0 or r['Id'] in bid_ids or type(r['Uuid']) is not int or r['Uuid'] <= 0:
            raise ValueError('Invalid or duplicate bid identity')
        bid_ids.add(r['Id'])
        if (r['Uuid'] in auction_map and auction_map[r['Uuid']] != au) or (au in auction_uuids and auction_uuids[au] != r['Uuid']):
            raise ValueError('Inconsistent auction UUID mapping')
        auction_map[r['Uuid']] = au
        auction_uuids[au] = r['Uuid']
    relation_keys = set()
    for r in rows(files['sql/auction-target-relations.jsonl']):
        if canonical(r['playerUuid']) != target or r['relation'] not in ('claimed_bid', 'coop_member'):
            raise ValueError('Relation ownership mismatch')
        key = (r['relation'], canonical(r['auctionUuid']))
        if key in relation_keys:
            raise ValueError('Duplicate auction relation')
        relation_keys.add(key)
        if key[1] not in auction_uuids:
            raise ValueError('Relation points to an unknown auction')
    for name in ('sql/metadata.json',):
        if name in files and json_load(files[name]).get('errors'):
            raise ValueError('SQL collection contains failures')
    return target


def safe_path(name):
    p = PurePosixPath(name)
    if not name or ':' in name or '\\' in name or p.is_absolute() or any(x in ('', '.', '..') for x in name.split('/')):
        raise ValueError('Unsafe archive path: ' + name)
    return p


def verify(path):
    files = {}
    root = None
    with zipfile.ZipFile(path) as archive:
        inventory = archive.infolist()
        if len(inventory) > 10000 or sum(i.file_size for i in inventory) > 512 * 1024 * 1024 or any(i.file_size > 128 * 1024 * 1024 for i in inventory):
            raise ValueError('Archive exceeds export size limits')
        for item in archive.infolist():
            p = safe_path(item.filename)
            if len(p.parts) < 2 or item.is_dir() or stat.S_IFMT(item.external_attr >> 16) not in (0, stat.S_IFREG):
                raise ValueError('Archive must contain regular files under one root')
            if root is None:
                root = p.parts[0]
            if p.parts[0] != root:
                raise ValueError('Archive has multiple roots')
            name = '/'.join(p.parts[1:])
            if name in files:
                raise ValueError('Duplicate archive entry')
            with archive.open(item) as entry:
                data = entry.read(128 * 1024 * 1024 + 1)
            if len(data) != item.file_size or len(data) > 128 * 1024 * 1024:
                raise ValueError('Archive entry exceeds declared size')
            files[name] = data
    sums = files.pop('SHA256SUMS', None)
    if sums is None:
        raise ValueError('Missing SHA256SUMS')
    listed = {}
    for line in sums.decode().splitlines():
        digest, name = line.split('  ', 1)
        safe_path(name)
        if name in listed or not re.fullmatch('[0-9a-f]{64}', digest):
            raise ValueError('Invalid or duplicate checksum entry')
        listed[name] = digest
    if set(listed) != set(files):
        raise ValueError('Checksum inventory does not match archive')
    for name, data in files.items():
        if hashlib.sha256(data).hexdigest() != listed[name]:
            raise ValueError('Checksum mismatch: ' + name)
    target = validate(files)
    return {'schema': SCHEMA, 'playerUuid': target, 'files': len(files) + 1}


def package(source, output):
    source, output = Path(source), Path(output)
    if source.is_symlink():
        raise ValueError('Source cannot be a symlink')
    if output.resolve().is_relative_to(source.resolve()):
        raise ValueError('ZIP must be outside source directory')
    files = {}
    for p in sorted(source.rglob('*')):
        if p.is_symlink():
            raise ValueError('Source contains a symlink')
        if p.is_file():
            name = p.relative_to(source).as_posix()
            safe_path(name)
            if name not in ('SHA256SUMS', 'format.json'):
                files[name] = p.read_bytes()
    identity = json_load(files['identity.json'])
    target = canonical(identity['minecraftUuid'])
    files['format.json'] = (json.dumps({'schema': SCHEMA, 'playerUuid': target, 'createdAtUtc': utc()}, indent=2) + '\n').encode()
    validate(files)
    # Preserve all collected fields; nested fields remain JSON in CSV cells.
    for name, data in list(files.items()):
        if name.endswith('.jsonl'):
            records = rows(data)
            fields = sorted({k for r in records for k in r})
            stream = io.StringIO(newline='')
            writer = csv.DictWriter(stream, fields)
            writer.writeheader()
            for record in records:
                writer.writerow({k: json.dumps(v, ensure_ascii=False) if isinstance(v, (dict, list)) else ("'" + v if isinstance(v, str) and v.startswith(('=', '+', '-', '@', '\t', '\r')) else v) for k, v in record.items()})
            files['readable/csv/' + name[:-6] + '.csv'] = stream.getvalue().encode()
    if 'README.txt' not in files:
        files['README.txt'] = ("Player export: " + (identity.get('minecraftName') or 'name not supplied') + "\n"
            + "Minecraft UUID: " + target + "\n"
            + "Readable CSV files: readable/csv/ (nested records are JSON cells).\n"
            + "Raw records: source JSON/JSONL files; empty JSONL means zero exported rows.\n"
            + "SQL and CQL bid datasets overlap; their row counts are not additive.\n"
            + "Source metadata retains collection timestamps; format.createdAtUtc is ZIP assembly time.\n"
            + "SQL datetimes are stored database values; boolean fields may be 0/1.\n"
            + "Raw NBT binary remains base64; CSV formula-like text is escaped.\n"
            + "Read coverage.json and source metadata for omissions and partial collections.\n"
            + "weekly_auctions_2 snapshots are outside repeatable collection scope and may retain identities/bids.\n"
            + "Older archive availability is not inferred; only explicit operator metadata asserts none.\n"
            + "SHA256SUMS covers every other file; verify the ZIP before use.\n"
            + "This private export contains personal data and should be stored securely.\n").encode()
    files['package-provenance.json'] = (json.dumps({'source': 'local collected directory', 'collectionMetadata': 'Preserved unchanged; format.createdAtUtc is assembly time only', 'playerUuid': target}, indent=2) + '\n').encode()
    files['SHA256SUMS'] = ''.join(hashlib.sha256(data).hexdigest() + '  ' + name + '\n' for name, data in sorted(files.items())).encode()
    # Exclusive creation prevents accidentally replacing an existing private export.
    fd = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, 'wb') as stream, zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED) as archive:
            for name, data in sorted(files.items()):
                info = zipfile.ZipInfo('player-export-' + target + '/' + name)
                info.external_attr = (stat.S_IFREG | 0o600) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, data)
        return verify(output)
    except BaseException:
        output.unlink(missing_ok=True)
        raise


def write_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')


def write_rows(path, data):
    path.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in data))


class SQL:
    def __init__(self, args, run=subprocess.run):
        self.args, self.run = args, run
        result = run(['kubectl', '--context', args.context, '--request-timeout=20s', '-n', args.namespace, 'get', 'secret', args.secret, '-o', 'json'], capture_output=True, text=True, timeout=30)
        if result.returncode:
            raise RuntimeError('Unable to read application DB credential')
        connection = base64.b64decode(json.loads(result.stdout)['data']['connection']).decode()
        parts = dict((k.strip().lower(), v.strip()) for part in connection.split(';') if '=' in part for k, v in [part.split('=', 1)])
        user = parts.get('user id') or parts.get('user') or parts.get('uid') or parts.get('username')
        password = parts.get('password') or parts.get('pwd')
        database = parts.get('database') or parts.get('initial catalog')
        if not user or not password or not database or not re.fullmatch('[A-Za-z0-9_]+', database):
            raise ValueError('Invalid application connection configuration')
        def ini(value):
            return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r') + '"'
        self.config = '[client]\nuser=' + ini(user) + '\npassword=' + ini(password) + '\ndatabase=' + ini(database) + '\nhost=127.0.0.1\nport=3306\nprotocol=TCP\n'

    def query(self, query):
        script = 'set -e; umask 077; f=$(mktemp /tmp/cofl-data-export.XXXXXX); trap \'rm -f "$f"\' EXIT; IFS= read -r auth; printf %s "$auth" | base64 -d > "$f"; mariadb --defaults-extra-file="$f" --batch --raw --skip-column-names --connect-timeout=10'
        remote = shlex.join(['docker', 'exec', '-i', self.args.sql_container, 'bash', '-c', script])
        payload = base64.b64encode(self.config.encode()).decode() + '\nSET SESSION max_statement_time=60; START TRANSACTION READ ONLY;\n' + query + '\nROLLBACK;\n'
        command = ['ssh', '-F', str(self.args.ansible_root / 'config/security/coflnet-bastion-ssh.conf'), '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', self.args.ssh_host, remote]
        result = self.run(command, input=payload, capture_output=True, text=True, timeout=90)
        if result.returncode:
            # Error text may contain credentials; never emit it.
            raise RuntimeError('Read-only SQL query failed')
        return rows(result.stdout.encode())


def collect_sql(client, target, out):
    out.mkdir()
    meta = client.query("SELECT JSON_OBJECT('database',DATABASE(),'replicaReadOnly',CAST(@@global.read_only AS CHAR),'snapshot',UTC_TIMESTAMP()); SELECT JSON_OBJECT('table',TABLE_NAME,'column',COLUMN_NAME,'type',DATA_TYPE) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('Auctions','Bids','Players','Users','AgreementAcceptances','Enchantment','NbtData','NBTLookups','NBTKeys','NBTValues','PlayerOptOutRequests') ORDER BY TABLE_NAME,ORDINAL_POSITION;")
    write_json(out / 'schema.json', meta)
    columns = {}
    for col in meta:
        if 'table' in col:
            columns.setdefault(col['table'], []).append(col)
    def ident(name):
        if not re.fullmatch('[A-Za-z_][A-Za-z0-9_]*', name):
            raise ValueError('Unsafe SQL identifier')
        return '`' + name + '`'
    def obj(table, alias):
        fields = []
        for col in columns[table]:
            value = alias + '.' + ident(col['column'])
            if col['type'] in ('varbinary', 'binary', 'blob', 'longblob', 'mediumblob', 'tinyblob'):
                value = 'TO_BASE64(' + value + ')'
            fields.extend(["'" + col['column'] + "'", value])
        return 'JSON_OBJECT(' + ','.join(fields) + ')'
    player_ids = "SELECT Id FROM Players WHERE UuId='" + target + "'"
    seller, bidder = 'a.SellerId IN (' + player_ids + ')', 'b.BidderId IN (' + player_ids + ')'
    owned = 'SELECT a.Id FROM Auctions a WHERE ' + seller
    users = "SELECT Id FROM Users WHERE MinecraftUuid='" + target + "'"
    queries = {
        'player': "SELECT " + obj('Players', 'p') + " FROM Players p WHERE p.UuId='" + target + "';",
        'auctions': 'SELECT ' + obj('Auctions', 'a') + ' FROM Auctions a WHERE ' + seller + ' ORDER BY a.Id;',
        'bids': "SELECT JSON_OBJECT('record'," + obj('Bids', 'b') + ",'auctionUuid',a.Uuid) FROM Bids b LEFT JOIN Auctions a ON a.Id=b.Uuid WHERE " + bidder + ' ORDER BY b.Id;',
        'bid-auction-context': "SELECT DISTINCT JSON_OBJECT('auctionUuid',a.Uuid,'itemTag',a.Tag,'itemName',a.ItemName,'startedAt',a.Start,'endedAt',a.End,'startingBid',a.StartingBid,'highestBidAmount',a.HighestBidAmount,'bin',a.Bin,'count',a.Count,'tier',a.Tier) FROM Bids b JOIN Auctions a ON a.Id=b.Uuid WHERE " + bidder + ';',
        'account': 'SELECT ' + obj('Users', 'u') + ' FROM Users u WHERE u.Id IN (' + users + ');',
        'agreement-acceptances': 'SELECT ' + obj('AgreementAcceptances', 'g') + ' FROM AgreementAcceptances g WHERE g.UserId IN (' + users + ');',
        'auction-enchantments': 'SELECT ' + obj('Enchantment', 'e') + ' FROM Enchantment e WHERE e.SaveAuctionId IN (' + owned + ');',
        'auction-nbt-binary': 'SELECT ' + obj('NbtData', 'n') + ' FROM NbtData n WHERE n.Id IN (SELECT a.NbtDataId FROM Auctions a WHERE ' + seller + ');',
        'auction-nbt-lookups': "SELECT JSON_OBJECT('AuctionId',n.AuctionId,'KeyId',n.KeyId,'key',k.Slug,'Value',n.Value,'stringValue',v.Value) FROM NBTLookups n LEFT JOIN NBTKeys k ON k.Id=n.KeyId LEFT JOIN NBTValues v ON v.Id=n.Value AND v.KeyId=n.KeyId WHERE n.AuctionId IN (" + owned + ');',
    }
    opt_out_available = 'PlayerOptOutRequests' in columns
    if opt_out_available:
        queries['opt-out-requests'] = 'SELECT ' + obj('PlayerOptOutRequests', 'o') + " FROM PlayerOptOutRequests o WHERE o.PlayerUuid='" + target + "';"
    collected = {}
    for name, query in queries.items():
        collected[name] = client.query(query)
        write_rows(out / (name + '.jsonl'), collected[name])
    if not opt_out_available:
        collected['opt-out-requests'] = []
        write_rows(out / 'opt-out-requests.jsonl', [])
    ids = {a['Id'] for a in collected['auctions']} | {b['record']['Uuid'] for b in collected['bids']}
    if any(type(i) is not int for i in ids):
        raise ValueError('Invalid auction foreign key')
    relations = []
    ordered = sorted(ids)
    for offset in range(0, len(ordered), 500):
        known = ','.join(str(i) for i in ordered[offset:offset + 500])
        for fk, relation in [('SaveAuctionId', 'claimed_bid'), ('SaveAuctionId1', 'coop_member')]:
            relations.extend(client.query("SELECT JSON_OBJECT('relation','" + relation + "','auctionUuid',a.Uuid,'playerUuid',u.value) FROM UuId u FORCE INDEX (`IX_UuId_" + fk + "`) JOIN Auctions a ON a.Id=u.`" + fk + "` WHERE u.`" + fk + "` IN (" + known + ") AND u.value='" + target + "';"))
    write_rows(out / 'auction-target-relations.jsonl', relations)
    write_json(out / 'metadata.json', {'playerUuid': target, 'exportedAtUtc': utc(), 'source': 'MariaDB', 'access': 'Existing application credential; read-only transactions; indexed foreign keys', 'counts': {k: len(v) for k, v in collected.items()}, 'errors': {}, 'snapshotMetadata': meta[0], 'optOutRequests': {'available': opt_out_available, 'source': 'PlayerOptOutRequests', 'dataset': 'opt-out-requests'}, 'relationCoverage': 'Target UUID on owned or bid-related SQL auctions; no global relation scan'})


def collect_mcconnect(client, target, out):
    out.mkdir()
    verified = "SELECT UserId FROM McIds WHERE AccountUuid='" + target + "' AND Verified=1"
    external = 'SELECT ExternalId FROM Users WHERE Id IN (' + verified + ')'
    # Explicit existing columns exclude any future credentials or verification tokens.
    columns = {'McIds': ['Id', 'AccountUuid', 'Verified', 'UserId', 'UpdatedAt', 'CreatedAt', 'LastRequestedAt'],
               'Challenges': ['Id', 'MinecraftUuid', 'AuctionUuid', 'BoughtBy', 'CreatedAt', 'BoughtAt', 'UserId', 'CompletedAt'],
               'Users': ['Id', 'ExternalId']}
    def obj(table, alias):
        fields = []
        for name in columns[table]:
            value = alias + '.`' + name + '`'
            if table == 'McIds' and name == 'UserId':
                value = 'CASE WHEN ' + alias + '.Verified=1 THEN ' + value + ' ELSE NULL END'
            if table == 'Challenges' and name == 'UserId':
                value = 'CASE WHEN ' + value + ' IN (' + external + ') THEN ' + value + ' ELSE NULL END'
            if table == 'Challenges' and name in ('MinecraftUuid', 'BoughtBy'):
                value = "CASE WHEN " + value + "='" + target + "' THEN " + value + ' ELSE NULL END'
            fields.extend(["'" + name + "'", value])
        return 'JSON_OBJECT(' + ','.join(fields) + ')'
    queries = {
        'account-links': 'SELECT ' + obj('McIds', 'm') + " FROM McIds m WHERE m.AccountUuid='" + target + "' ORDER BY m.Id;",
        'verification-challenges': 'SELECT ' + obj('Challenges', 'c') + " FROM Challenges c WHERE c.MinecraftUuid='" + target + "' OR c.BoughtBy='" + target + "' ORDER BY c.Id;",
        'verified-link-users': 'SELECT ' + obj('Users', 'u') + ' FROM Users u WHERE u.Id IN (' + verified + ') ORDER BY u.Id;',
    }
    meta = {'playerUuid': target, 'source': 'McConnect MariaDB', 'exportedAtUtc': utc(), 'counts': {}, 'errors': {}, 'redaction': 'Unverified linked user references and non-target UUIDs omitted; verified users only; fixed field allowlist excludes secrets'}
    try:
        for name, query in queries.items():
            records = client.query(query)
            write_rows(out / (name + '.jsonl'), records)
            meta['counts'][name] = len(records)
    except Exception as error:
        meta['errors']['collection'] = type(error).__name__
        raise
    finally:
        write_json(out / 'metadata.json', meta)


def collect(args):
    if args.uuid:
        target = canonical(args.uuid)
        identity = {'minecraftUuid': target, 'minecraftName': args.name, 'identitySource': 'caller supplied UUID', 'resolvedAtUtc': utc()}
    else:
        if not args.name or not re.fullmatch('[A-Za-z0-9_]{1,16}', args.name):
            raise ValueError('Provide a Minecraft name or --uuid')
        url = 'https://api.mojang.com/users/profiles/minecraft/' + args.name
        with urllib.request.urlopen(url, timeout=20) as response:
            result = json.load(response)
        target = canonical(result['id'])
        identity = {'minecraftUuid': target, 'minecraftName': result['name'], 'identitySource': url, 'resolvedAtUtc': utc()}
    with tempfile.TemporaryDirectory(prefix='cofl-player-export-', dir=args.work_dir) as temporary:
        root = Path(temporary)
        write_json(root / 'identity.json', identity)
        collect_sql(SQL(args), target, root / 'sql')
        coverage = {'sql': 'collected', 'olderS3Archives': 'operator confirms none available; no scan performed' if args.no_older_archives else 'excluded from scope; availability not assessed; no scan performed', 'publicHttp': 'not collected; SQL and scoped CQL provide primary records'}
        coverage['weeklyAuctionSnapshots'] = 'sky_auctions.weekly_auctions_2 not collected; snapshots may retain player identities and serialized bids; assess before complete erasure'
        coverage['minecraftLinking'] = 'not requested; enable --mcconnect'
        if args.mcconnect:
            mc_args = argparse.Namespace(**vars(args))
            mc_args.secret, mc_args.sql_container = args.mcconnect_secret, args.mcconnect_container
            try:
                collect_mcconnect(SQL(mc_args), target, root / 'minecraft-linking')
                coverage['minecraftLinking'] = 'collected; verified target links and redacted challenges'
            except Exception:
                coverage['minecraftLinking'] = 'unavailable or incomplete; see metadata if present'
        if args.cql:
            helper = Path(__file__).with_name('player-export-cql.py')
            result = subprocess.run([str(args.cql_python), str(helper), '--uuid', target, '--name', identity['minecraftName'] or '', '--output', str(root / 'scylla'), '--host', args.cql_host], capture_output=True, text=True, timeout=1800)
            cql_metadata = root / 'scylla/metadata.json'
            cql_complete = result.returncode == 0 and cql_metadata.exists() and not json_load(cql_metadata.read_bytes()).get('errors') and json_load(cql_metadata.read_bytes()).get('completedAtUtc')
            coverage['scylla'] = 'collected with per-dataset coverage' if cql_complete else 'unavailable or incomplete; see scylla metadata if present'
        else:
            coverage['scylla'] = 'not requested; enable --cql with secured existing client'
        write_json(root / 'coverage.json', coverage)
        return package(root, args.output)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('package', help='Package an existing collection without contacting services')
    p.add_argument('source', type=Path)
    p.add_argument('--output', type=Path, required=True)
    p = sub.add_parser('verify', help='Verify inventory, hashes, paths and exact SQL ownership')
    p.add_argument('zip', type=Path)
    p = sub.add_parser('export', help='Collect read-only SQL and optional scoped CQL, then package')
    p.add_argument('name', nargs='?')
    p.add_argument('--uuid')
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--work-dir', type=Path)
    p.add_argument('--ansible-root', type=Path, default=next((p / 'ansible' for p in Path(__file__).resolve().parents if (p / 'ansible').is_dir()), Path.cwd()))
    p.add_argument('--context', default='talos-eu')
    p.add_argument('--namespace', default='sky')
    p.add_argument('--secret', default='sky-api-db')
    p.add_argument('--ssh-host', default='mariadb-sky-77-via-a')
    p.add_argument('--sql-container', default='mariadb-sky-1')
    p.add_argument('--no-older-archives', action='store_true', help='Record operator assertion that no older archives are available')
    p.add_argument('--mcconnect', action='store_true')
    p.add_argument('--mcconnect-secret', default='sky-mc-connect-db')
    p.add_argument('--mcconnect-container', default='sky-micro-sky-micro-1')
    p.add_argument('--cql', action='store_true')
    p.add_argument('--cql-host', default='38.242.207.102')
    p.add_argument('--cql-python', type=Path, default=Path.home() / 'programs/cqlsh/bin/python')
    args = parser.parse_args()
    try:
        result = verify(args.zip) if args.command == 'verify' else package(args.source, args.output) if args.command == 'package' else collect(args)
        print(json.dumps(result))
    except Exception as error:
        print(type(error).__name__ + ': ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
