import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('plan', Path(__file__).with_name('player-erasure-plan.py'))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
U = '0123456789abcdef0123456789abcdef'


class ExactRowPlanTests(unittest.TestCase):
    def test_prepare_binds_verified_export_and_describes_conditional_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'source'
            (source / 'sql').mkdir(parents=True)
            (source / 'identity.json').write_text(json.dumps({'minecraftUuid': U}))
            (source / 'sql/player.jsonl').write_text(json.dumps({
                'Id': 1, 'UuId': U, 'Name': 'Synthetic', 'UpdatedAt': '2026-10-02T00:00:00Z'}) + '\n')
            for name in ('auctions', 'bids', 'auction-target-relations'):
                (source / ('sql/' + name + '.jsonl')).write_text('')
            archive = root / 'synthetic.zip'
            p.exporter.package(source, archive)
            plan = p.prepare(archive)
            self.assertEqual(plan['playerUuid'], U)
            self.assertEqual(plan['exportSha256'], p.digest(archive.read_bytes()))
            self.assertFalse(plan['applied'])
            r2 = next(phase for phase in plan['phases'] if phase['store'] == 'R2 auction archive')
            self.assertEqual(r2['enabledWhen'], ['ARCHIVE:ENABLED=true', 'S3:ENABLED=true'])

    def test_records_keep_exact_keys_and_snapshot_hash(self):
        record = {'bidderuuid': U, 'timestamp': '2026-10-02 00:00:00.123Z', 'amount': 1}
        one = p.exact_rows([record], U, 'sky_auctions.bids')[0]
        self.assertEqual(one['key'], {'bidderuuid': U, 'timestamp': '2026-10-02T00:00:00.123000+00:00'})
        changed = p.exact_rows([dict(record, amount=2)], U, 'sky_auctions.bids')[0]
        self.assertNotEqual(one['expectedRecordSha256'], changed['expectedRecordSha256'])
        self.assertEqual(one['sourceRow'], 1)

    def test_foreign_player_and_duplicate_keys_are_rejected(self):
        record = {'bidderuuid': U, 'timestamp': '2026-10-02T00:00:00Z'}
        with self.assertRaises(ValueError):
            p.exact_rows([dict(record, bidderuuid='f'*32)], U, 'sky_auctions.bids')
        with self.assertRaises(ValueError):
            p.exact_rows([record, record], U, 'sky_auctions.bids')

    def test_flip_key_must_have_exact_integer_uid_and_utc(self):
        record = {'flipper': U, 'selltime': '2026-10-02T00:00:00Z', 'uid': 42}
        self.assertEqual(p.exact_rows([record], U, 'flips.flips')[0]['key']['uid'], 42)
        for changed in [dict(record, uid='42'), dict(record, uid=True), dict(record, selltime='2026-10-02T00:00:00')]:
            with self.assertRaises(ValueError):
                p.exact_rows([changed], U, 'flips.flips')


if __name__ == '__main__':
    unittest.main()
