#!/usr/bin/env python3
"""Prepare an export-bound erasure plan; never connects to or changes a database."""
import argparse
import datetime as dt
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import zipfile

spec = importlib.util.spec_from_file_location('player_export', Path(__file__).with_name('player-export.py'))
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def exact_rows(records, target, table):
    key_fields = ('bidderuuid', 'timestamp') if table == 'sky_auctions.bids' else ('flipper', 'selltime', 'uid')
    seen, result = set(), []
    for number, record in enumerate(records, 1):
        if exporter.canonical(record[key_fields[0]]) != target:
            raise ValueError('Foreign player in ' + table)
        timestamp = dt.datetime.fromisoformat(record[key_fields[1]].replace('Z', '+00:00'))
        if timestamp.utcoffset() != dt.timedelta(0):
            raise ValueError('Scylla key timestamp must include UTC timezone')
        key = {key_fields[0]: target, key_fields[1]: timestamp.isoformat()}
        if len(key_fields) == 3:
            if type(record['uid']) is not int:
                raise ValueError('Flip UID must be an integer')
            key['uid'] = record['uid']
        signature = tuple(key.values())
        if signature in seen:
            raise ValueError('Duplicate Scylla key in ' + table)
        seen.add(signature)
        result.append({'key': key, 'sourceRow': number,
                       'expectedRecordSha256': digest(json.dumps(record, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode())})
    return result


def prepare(path):
    if path.stat().st_size > 512 * 1024 * 1024:
        raise ValueError('Compressed export exceeds size limit')
    blob = path.read_bytes()
    verified = exporter.verify(io.BytesIO(blob))
    target = verified['playerUuid']
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        files = {'/'.join(item.filename.split('/')[1:]): archive.read(item) for item in archive.infolist()}
    phases = [{'store': 'MariaDB indexer', 'action': 'anonymize exported identities, remove exported target affiliations, delete the Players row and unlink Minecraft accounts',
               'counts': {name: len(exporter.rows(files['sql/' + name + '.jsonl'])) for name in ('auctions', 'bids', 'auction-target-relations')},
               'requirements': ['Indexer privacy check must pass against the write database immediately before apply.',
                                'A persisted indexer opt-out request must match the player UUID; running ingestion and name updates enforce the request table.',
                                'privacy erase verifies ZIP checksums and compares the export with live SQL data before applying; the printed export SHA-256 is not an operator authorization signature.']},
              {'store': 'R2 auction archive', 'action': 'rewrite discovered player identities through privacy erase EXPORT.zip before SQL erasure',
               'enabledWhen': ['ARCHIVE:ENABLED=true', 'S3:ENABLED=true'],
               'status': 'configuration and archive coverage not assessed offline',
               'requirements': ['Both flags must be enabled in the CLI configuration; otherwise the archive is untouched.',
                                'Discover numbered seller/bidder auctions through indexes and MariaDB, and scan unmarked Scylla-sourced archive parts by UUID.',
                                'Verify rewritten parts and update hashes before deleting player archive index rows; pending imports block erasure.']}]
    for filename, table in [('scylla/auction-bids.jsonl', 'sky_auctions.bids'), ('scylla/auction-flips.jsonl', 'flips.flips')]:
        if filename not in files:
            phases.append({'store': table, 'status': 'not assessed: export has no source file'})
            continue
        records = exporter.rows(files[filename])
        phases.append({'store': table, 'action': 'prepare exact-row deletion', 'sourceFile': filename,
                       'sourceSha256': digest(files[filename]), 'rows': exact_rows(records, target, table),
                       'status': 'prepared only; no Scylla apply command implemented',
                       'requirements': ['Deploy source-service ingestion protection before erasure.',
                                        'Re-read and compare each full live row and target partition inventory against the export before deleting exact keys.']})
    phases.append({'store': 'sky_auctions.weekly_auctions_2',
                   'status': 'not assessed: this export does not contain all physical auction snapshots',
                   'sourceFiles': ['sql/auctions.jsonl', 'sql/bid-auction-context.jsonl'],
                   'requirements': ['Collect exact indexed auction rows before changing their seller/profile/highest-bidder fields or serialized bids.',
                                    'Preserve unrelated bidders and item data; enforce opt-out protection at the owning service.']})
    return {'schema': 'coflnet-player-erasure-plan/v1', 'playerUuid': target,
            'createdAtUtc': exporter.utc(), 'exportSha256': digest(blob), 'exportFile': path.name,
            'applied': False, 'phases': phases,
            'limits': ['SQL mutations and prepared Scylla keys are bounded by the verified export; archive targets are discovered live.',
                       'SQL economic auction records and unrelated bidders remain.',
                       'Item metadata, other services, logs and backups are outside this prepared auction workflow.',
                       'Do not treat SQL-only anonymization as complete cross-service erasure.']}


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    plan = prepare(args.export)
    with args.output.open('x') as output:
        json.dump(plan, output, indent=2)
        output.write('\n')
    print(json.dumps({'plan': str(args.output), 'playerUuid': plan['playerUuid'], 'exportSha256': plan['exportSha256'], 'applied': False}))


if __name__ == '__main__':
    main()
