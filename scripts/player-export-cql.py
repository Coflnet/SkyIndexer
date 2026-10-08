#!/usr/bin/env python3
"""Scoped SELECT exports using the operator's existing secured Cassandra client."""
import argparse
import configparser
import datetime
import json
import os
from pathlib import Path
import ssl
import uuid
from cassandra.auth import PlainTextAuthProvider
from cassandra.cluster import Cluster
from cassandra.policies import WhiteListRoundRobinPolicy

os.umask(0o077)
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--uuid', required=True)
p.add_argument('--name', default='')
p.add_argument('--output', type=Path, required=True)
p.add_argument('--host', required=True)
a = p.parse_args()
u = uuid.UUID(a.uuid).hex
g = str(uuid.UUID(u))
a.output.mkdir(mode=0o700)
cfg = configparser.ConfigParser()
cfg.read(Path.home() / '.cassandra/cqlshrc')
ctx = ssl.create_default_context(cafile=os.path.expanduser(cfg['ssl']['certfile']))
ctx.check_hostname = False  # Existing bastion tunnel uses the remote certificate.
ctx.load_cert_chain(os.path.expanduser(cfg['ssl']['usercert']), os.path.expanduser(cfg['ssl']['userkey']))
authcfg = configparser.ConfigParser()
authcfg.read(Path.home() / '.cassandra/credentials')
auth = PlainTextAuthProvider(authcfg['PlainTextAuthProvider']['username'], authcfg['PlainTextAuthProvider']['password'])
cluster = Cluster([a.host], auth_provider=auth, port=9042, ssl_context=ctx, protocol_version=4, load_balancing_policy=WhiteListRoundRobinPolicy([a.host]), schema_metadata_enabled=False, token_metadata_enabled=False, connect_timeout=15)
meta = {'playerUuid': u, 'startedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'access': 'SELECT only; exact partition keys; existing TLS and credentials', 'datasets': {}, 'errors': {}}

def persist():
    (a.output / 'metadata.json').write_text(json.dumps(meta, indent=2) + '\n')

def quoted(value):
    return "'" + value.replace("'", "''") + "'"

try:
    session = cluster.connect()
    session.default_timeout = 30
    session.default_fetch_size = 500
    def query(name, keyspace, table, where, fields='*'):
        try:
            records = [json.loads(row[0]) for row in session.execute(f'SELECT JSON {fields} FROM {keyspace}.{table} WHERE {where};')]
            (a.output / (name + '.jsonl')).write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in records))
            meta['datasets'][name] = {'source': keyspace + '.' + table, 'predicate': where, 'rows': len(records), 'queriedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()}
            return records
        except Exception as error:
            meta['errors'][name] = {'type': type(error).__name__}  # Driver messages can expose private connection details.
            return []
        finally:
            persist()
    for table, col, value in [('skyplayerstate', 'playerid', quoted(u)), ('skills', 'player', g), ('transactions', 'playeruuid', g), ('bazaar_buy_records2', 'playeruuid', g), ('locationperiods', 'playeruuid', quoted(u)), ('historyperiods', 'playeruuid', quoted(u)), ('historyperiods2', 'playeruuid', quoted(u)), ('task_player_stats', 'playeruuid', quoted(u))]:
        query('state-' + table, 'sky_items_movement', table, col + '=' + value)
    if a.name:
        query('state-skyplayerstate-name', 'sky_items_movement', 'skyplayerstate', 'playerid=' + quoted(a.name))
    for year in range(2019, datetime.datetime.now(datetime.timezone.utc).year + 1):
        if a.name:
            query('coin-counters-' + str(year), 'sky_items_movement', 'coin_counters', f'user_id={quoted(a.name)} AND year={year}')
        query('bazaar-flips-' + str(year), 'sky_items_movement', 'bazaar_flips', f'playeruuid={g} AND year={year}')
    active = query('profile-active-history', 'sky_player_info', 'active_player_profiles', 'playerid=' + g)
    query('profile-hypixel-history', 'sky_player_info', 'hypixel_player_profiles', 'playerid=' + g)
    query('auction-bids', 'sky_auctions', 'bids', 'bidderuuid=' + g)
    query('auction-flips', 'flips', 'flips', 'flipper=' + g)
    query('discord-link', 'discord_bot', 'account_info_mc', 'minecraftuuid=' + g)
    query('nec-import', 'sky_mod', 'necuser', 'uuid=' + quoted(u), 'uuid,email,claimedat')
    if a.name:
        query('youtuber-name', 'sky_mod', 'youtubers', 'namelower=' + quoted(a.name.lower()))
    profiles = set()
    def add_profile(value, source):
        try:
            parsed = uuid.UUID(value)
            if parsed.int in (0, 1) or parsed.hex == u:
                meta.setdefault('skippedProfileIdentifiers', []).append({'source': source, 'value': value})
            else:
                profiles.add(parsed.hex)
        except (ValueError, TypeError, AttributeError):
            meta.setdefault('malformedProfileIdentifiers', []).append({'source': source, 'value': value})
    for record in active:
        if record.get('profileid'):
            add_profile(record['profileid'], 'active_player_profiles')
    for filename in ('auctions.jsonl', 'bids.jsonl'):
        for line in (a.output.parent / 'sql' / filename).read_text().splitlines():
            row = json.loads(line)
            row = row.get('record', row)
            if row.get('ProfileId'):
                add_profile(row['ProfileId'], filename)
    for profile in sorted(profiles):
        profile_uuid = str(uuid.UUID(profile))
        for table, key in [('hypixel_skyblock_profiles', 'part'), ('hypixel_museum', 'playerid')]:
            query('profile-' + table + '-' + profile, 'sky_player_info', table, f'profileid={profile_uuid} AND {key}={g}')
        for table in ('player_storage', 'player_storage2'):
            query('state-' + table + '-' + profile, 'sky_items_movement', table, f'playerid={g} AND profileid={profile_uuid}')
    meta['knownProfileIds'] = sorted(profiles)
    meta['omitted'] = ['Session-keyed mod_stay_logged_out (session IDs unavailable)', 'Unindexed global UUID relations', 'Redemption keys and unverified account references', 'sky_auctions.weekly_auctions_2 auction snapshots (player identities and serialized bids); assess before complete erasure']
    meta['completedAtUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
finally:
    persist()
    cluster.shutdown()
