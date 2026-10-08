import importlib.util
import json
import os
import re
from pathlib import Path
import stat
import tempfile
from types import SimpleNamespace
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('exporter', Path(__file__).with_name('player-export.py'))
e = importlib.util.module_from_spec(spec)
spec.loader.exec_module(e)
U = '0123456789abcdef0123456789abcdef'
A = '123456789abcdef0123456789abcdef0'


class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'source'
        self.source.mkdir()
        (self.source / 'sql').mkdir()
        e.write_json(self.source / 'identity.json', {'minecraftUuid': U, 'resolvedAtUtc': '2020-01-01T00:00:00Z'})
        self.records = {'player': [{'Id': 7, 'UuId': U, 'Name': 'Example', 'UpdatedAt': '2020-01-01 00:00:00', 'legacyField': 99}], 'auctions': [{'Id': 4, 'SellerId': 7, 'AuctioneerId': U, 'Uuid': A, 'ProfileId': '2' * 32, 'Start': '2020-01-01 00:00:00', 'End': '2020-01-02 00:00:00', 'HighestBidAmount': 100, 'StartingBid': 50, 'UId': 123}], 'bids': [{'record': {'Id': 5, 'Uuid': 4, 'BidderId': 7, 'Bidder': U, 'ProfileId': '2' * 32, 'Amount': 100, 'Timestamp': '2020-01-01 00:00:00'}, 'auctionUuid': A}], 'auction-target-relations': [{'playerUuid': U, 'auctionUuid': A, 'relation': 'claimed_bid'}]}
        self.save()
        self.zip = self.root / 'export.zip'

    def save(self):
        for name, records in self.records.items():
            e.write_rows(self.source / 'sql' / (name + '.jsonl'), records)

    def mutate_zip(self, transform):
        with zipfile.ZipFile(self.zip) as z:
            records = [(i, z.read(i)) for i in z.infolist()]
        with zipfile.ZipFile(self.zip, 'w') as z:
            for info, data in records:
                info, data = transform(info, data)
                z.writestr(info, data)

    def test_round_trip_preserves_fields_time_and_private_files(self):
        e.package(self.source, self.zip)
        self.assertEqual(e.verify(self.zip)['playerUuid'], U)
        self.assertEqual(stat.S_IMODE(self.zip.stat().st_mode), 0o600)
        with zipfile.ZipFile(self.zip) as z:
            names = z.namelist()
            player = next(n for n in names if n.endswith('sql/player.jsonl'))
            self.assertEqual(e.rows(z.read(player))[0]['legacyField'], 99)
            identity = next(n for n in names if n.endswith('identity.json'))
            self.assertEqual(json.loads(z.read(identity))['resolvedAtUtc'], '2020-01-01T00:00:00Z')
            self.assertTrue(all(stat.S_IMODE(i.external_attr >> 16) == 0o600 for i in z.infolist()))

    def test_cross_player_records_rejected(self):
        for dataset, field in [('player', 'UuId'), ('auctions', 'AuctioneerId'), ('auctions', 'SellerId'), ('bids', 'BidderId'), ('bids', 'Bidder'), ('auction-target-relations', 'playerUuid')]:
            with self.subTest(dataset=dataset, field=field):
                record = self.records[dataset][0]
                record = record['record'] if dataset == 'bids' else record
                old = record[field]
                record[field] = 88 if field in ('SellerId', 'BidderId') else 'f' * 32
                self.save()
                with self.assertRaises(ValueError):
                    e.package(self.source, self.zip)
                record[field] = old
        self.save()

    def test_missing_bid_auction_uuid_rejected(self):
        self.records['bids'][0]['auctionUuid'] = None
        self.save()
        with self.assertRaises((ValueError, AttributeError)):
            e.package(self.source, self.zip)

    def test_required_empty_files_supported(self):
        self.records = {k: v if k == 'player' else [] for k, v in self.records.items()}
        self.save()
        e.package(self.source, self.zip)
        e.verify(self.zip)

    def test_source_symlink_rejected(self):
        (self.source / 'leak').symlink_to('/etc/passwd')
        with self.assertRaises(ValueError):
            e.package(self.source, self.zip)

    def test_tampering_rejected(self):
        e.package(self.source, self.zip)
        self.mutate_zip(lambda i, d: (i, d + b'x') if i.filename.endswith('identity.json') else (i, d))
        with self.assertRaises(ValueError):
            e.verify(self.zip)

    def test_traversal_rejected(self):
        e.package(self.source, self.zip)
        with zipfile.ZipFile(self.zip, 'a') as z:
            z.writestr('root/../leak', b'x')
        with self.assertRaises(ValueError):
            e.verify(self.zip)

    def test_duplicate_and_symlink_rejected(self):
        e.package(self.source, self.zip)
        with zipfile.ZipFile(self.zip, 'a') as z:
            i = zipfile.ZipInfo('root/link')
            i.external_attr = (stat.S_IFLNK | 0o777) << 16
            z.writestr(i, b'/etc/passwd')
        with self.assertRaises(ValueError):
            e.verify(self.zip)

    def test_failure_metadata_rejected(self):
        e.write_json(self.source / 'sql/metadata.json', {'errors': {'auctions': 'failed'}})
        with self.assertRaises(ValueError):
            e.package(self.source, self.zip)

    def test_transport_credential_stdin_readonly_and_trap(self):
        import base64
        calls = []
        def fake(cmd, **kw):
            calls.append((cmd, kw))
            if cmd[0] == 'kubectl':
                secret = base64.b64encode(b'User Id=user;Password=private;Database=sky').decode()
                return SimpleNamespace(returncode=0, stdout=json.dumps({'data': {'connection': secret}}))
            return SimpleNamespace(returncode=0, stdout='{"Id":1}\n')
        args = SimpleNamespace(context='test', namespace='sky', secret='app', ansible_root=Path('/workspace/ansible'), ssh_host='replica', sql_container='db')
        client = e.SQL(args, fake)
        self.assertEqual(client.query('SELECT 1;'), [{'Id': 1}])
        command, kw = calls[1]
        self.assertNotIn('private', ' '.join(command))
        self.assertIn('START TRANSACTION READ ONLY;', kw['input'])
        self.assertIn('ROLLBACK;', kw['input'])
        self.assertIn('trap', command[-1])
        self.assertIn('umask 077', command[-1])
        self.assertIn('rm -f', command[-1])

    def test_collection_uses_exact_foreign_keys_and_preserves_rows(self):
        queries = []
        sample = self.records
        class Fake:
            def query(self, query):
                queries.append(query)
                if 'information_schema.COLUMNS' in query:
                    return [{'snapshot': 'original'}] + [{'table': t, 'column': 'Id', 'type': 'int'} for t in ('Players', 'Auctions', 'Bids', 'Users', 'AgreementAcceptances', 'Enchantment', 'NbtData')]
                if 'FROM Players p' in query:
                    return sample['player']
                if 'FROM Auctions a WHERE' in query and 'ORDER BY a.Id' in query:
                    return sample['auctions']
                if 'FROM Bids b LEFT JOIN Auctions' in query:
                    return sample['bids']
                if 'FORCE INDEX' in query:
                    return [dict(sample['auction-target-relations'][0], relation='coop_member' if 'SaveAuctionId1' in query else 'claimed_bid')]
                return []
        out = self.root / 'collected-sql'
        e.collect_sql(Fake(), U, out)
        relation_queries = [q for q in queries if 'FORCE INDEX' in q]
        self.assertEqual(len(relation_queries), 2)
        self.assertTrue(all(' IN (4)' in q and "u.value='" + U + "'" in q for q in relation_queries))
        self.assertIn('IX_UuId_SaveAuctionId1', relation_queries[1])
        self.assertFalse(any(' UNION ' in q for q in queries))
        self.assertEqual(e.rows((out / 'player.jsonl').read_bytes()), sample['player'])
        self.assertEqual(len(e.rows((out / 'auction-target-relations.jsonl').read_bytes())), 2)

    def test_duplicate_entry_rejected(self):
        import warnings
        e.package(self.source, self.zip)
        with zipfile.ZipFile(self.zip, 'a') as z, warnings.catch_warnings():
            warnings.simplefilter('ignore')
            z.writestr(z.namelist()[0], b'duplicate')
        with self.assertRaises(ValueError):
            e.verify(self.zip)

    def test_formula_escaped_only_in_csv(self):
        self.records['player'][0]['Name'] = '=danger()'
        self.save()
        e.package(self.source, self.zip)
        with zipfile.ZipFile(self.zip) as z:
            csv_name = next(n for n in z.namelist() if n.endswith('csv/sql/player.csv'))
            raw_name = next(n for n in z.namelist() if n.endswith('/sql/player.jsonl'))
            self.assertIn("'=danger()", z.read(csv_name).decode())
            self.assertEqual(e.rows(z.read(raw_name))[0]['Name'], '=danger()')

    def test_duplicate_json_fields_rejected(self):
        (self.source / 'identity.json').write_text('{"minecraftUuid":"' + U + '","minecraftUuid":"' + U + '"}')
        with self.assertRaises(ValueError):
            e.package(self.source, self.zip)

    def test_player_required_and_relation_must_be_known(self):
        self.records['auction-target-relations'][0]['auctionUuid'] = 'f' * 32
        self.save()
        with self.assertRaises(ValueError):
            e.package(self.source, self.zip)
        self.records['auction-target-relations'] = []
        self.records['player'] = []
        self.save()
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            e.package(self.source, self.zip)

    def test_mcconnect_scope_and_secret_field_allowlist(self):
        queries = []
        class Fake:
            def query(self, query):
                queries.append(query)
                return []
        e.collect_mcconnect(Fake(), U, self.root / 'links')
        self.assertEqual(len(queries), 3)
        self.assertTrue(all(U in q for q in queries))
        self.assertIn('Verified=1', queries[0])
        self.assertIn('ELSE NULL', queries[1])
        self.assertNotIn('Password', ''.join(queries))
        self.assertNotIn('Token', ''.join(queries))

    def test_zip_limits_fail_before_reading(self):
        from unittest.mock import patch
        class FakeZip:
            def __enter__(self):
                return self
            def __exit__(self, *args):
                pass
            def infolist(self):
                return [SimpleNamespace(file_size=128 * 1024 * 1024 + 1)]
        with patch.object(e.zipfile, 'ZipFile', return_value=FakeZip()):
            with self.assertRaisesRegex(ValueError, 'size limits'):
                e.verify(self.zip)

    def test_case_conflicting_json_properties_rejected(self):
        with self.assertRaises(ValueError):
            e.json_load('{"Name":"one","name":"two"}')

    def test_raw_snapshot_keys_required(self):
        for dataset, field in [('player', 'Name'), ('auctions', 'ProfileId'), ('bids', 'Timestamp')]:
            with self.subTest(dataset=dataset):
                record = self.records[dataset][0]
                record = record['record'] if dataset == 'bids' else record
                saved = record.pop(field)
                self.save()
                with self.assertRaisesRegex(ValueError, 'required raw fields'):
                    e.package(self.source, self.zip)
                record[field] = saved
        self.save()

    def test_duplicate_relation_rejected(self):
        self.records['auction-target-relations'] *= 2
        self.save()
        with self.assertRaisesRegex(ValueError, 'Duplicate auction relation'):
            e.package(self.source, self.zip)

    def test_opt_out_table_available_and_absent(self):
        for available in (False, True):
            with self.subTest(available=available):
                queries = []
                opt_out = {'PlayerUuid': U, 'RequestedAtUtc': '2026-10-02 00:00:00.000001'}
                class Fake:
                    def query(self, query):
                        queries.append(query)
                        if 'information_schema.COLUMNS' in query:
                            schema = [{'snapshot': 'original'}] + [{'table': t, 'column': 'Id', 'type': 'int'} for t in ('Players', 'Auctions', 'Bids', 'Users', 'AgreementAcceptances', 'Enchantment', 'NbtData')]
                            if available:
                                schema += [{'table': 'PlayerOptOutRequests', 'column': col, 'type': 'datetime' if col.endswith('AtUtc') else 'char'} for col in opt_out]
                            return schema
                        if 'FROM PlayerOptOutRequests o' in query:
                            return [opt_out]
                        return []
                out = self.root / ('sql-available' if available else 'sql-absent')
                e.collect_sql(Fake(), U, out)
                data = e.rows((out / 'opt-out-requests.jsonl').read_bytes())
                self.assertEqual(data, [opt_out] if available else [])
                metadata = e.json_load((out / 'metadata.json').read_bytes())
                self.assertEqual(metadata['optOutRequests']['available'], available)
                opt_queries = [q for q in queries if 'FROM PlayerOptOutRequests o' in q]
                self.assertEqual(len(opt_queries), int(available))
                if available:
                    self.assertIn("WHERE o.PlayerUuid='" + U + "'", opt_queries[0])
                    self.assertTrue(all("'" + key + "'" in opt_queries[0] for key in opt_out))
                self.assertFalse(any(re.search(r'\b(?:CREATE|ALTER|INSERT|UPDATE|DELETE)\b', q) for q in queries))

    def test_package_generates_readme_or_preserves_existing_one(self):
        e.package(self.source, self.zip)
        with zipfile.ZipFile(self.zip) as z:
            name = next(n for n in z.namelist() if n.endswith('/README.txt'))
            readme = z.read(name).decode()
            self.assertIn(U, readme)
            self.assertIn('row counts are not additive', readme)
            self.assertIn('weekly_auctions_2', readme)
            self.assertIn('availability is not inferred', readme)
        original = b'Original source collection notes, preserved verbatim.\n'
        (self.source / 'README.txt').write_bytes(original)
        other_zip = self.root / 'with-source-readme.zip'
        e.package(self.source, other_zip)
        with zipfile.ZipFile(other_zip) as z:
            name = next(n for n in z.namelist() if n.endswith('/README.txt'))
            self.assertEqual(z.read(name), original)

    def test_transport_failure_redacts_message(self):
        client = object.__new__(e.SQL)
        client.args = SimpleNamespace(ansible_root=Path('/workspace'), ssh_host='replica', sql_container='db')
        client.config = 'private'
        client.run = lambda *a, **kw: SimpleNamespace(returncode=1, stdout='', stderr='private')
        with self.assertRaisesRegex(RuntimeError, '^Read-only SQL query failed$'):
            client.query('SELECT 1;')


if __name__ == '__main__':
    unittest.main()
