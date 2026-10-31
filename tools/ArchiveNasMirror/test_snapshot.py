"""Harness for snapshot.sh with stubbed zfs/zpool on PATH. Run: python3 -I -m unittest"""
import glob
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))

ZFS_STUB = r"""#!/usr/bin/env bash
echo "zfs $*" >> "$STUB_LOG"
case "$1" in
  get) printf 'keystatus\t%s\nmounted\t%s\n' "${STUB_KEYSTATUS:-available}" "${STUB_MOUNTED:-yes}" ;;
  list)
    if [[ " $* " == *" snapshot "* ]]; then
      name=${@: -1}; name=${name#*@}
      [ -d "$STUB_MOUNT/.zfs/snapshot/$name" ]; exit $?
    fi
    if [[ " $* " == *"name,mountpoint"* ]]; then printf 'storage/backup\t%s\n' "$STUB_MOUNT"; else echo storage/backup; fi ;;
  snapshot)
    name=${@: -1}; name=${name#*@}
    mkdir -p "$STUB_MOUNT/.zfs/snapshot/$name"
    (cd "$STUB_MOUNT" && tar --exclude=./.zfs -cf - .) | (cd "$STUB_MOUNT/.zfs/snapshot/$name" && tar xf -)
    [ -n "${STUB_CORRUPT:-}" ] && printf 'X' | dd of="$STUB_MOUNT/.zfs/snapshot/$name/$STUB_CORRUPT" bs=1 count=1 conv=notrunc 2>/dev/null
    [ -n "${STUB_DELETE:-}" ] && rm "$STUB_MOUNT/.zfs/snapshot/$name/$STUB_DELETE"
    exit 0 ;;
  hold) exit 0 ;;
esac
"""
ZPOOL_STUB = r"""#!/usr/bin/env bash
echo "zpool $*" >> "$STUB_LOG"
echo "${STUB_ZPOOL:-pool 'storage' is healthy}"
"""


class SnapshotScriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="snap-test-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.bin = os.path.join(self.tmp, "bin")
        self.mount = os.path.join(self.tmp, "mnt")
        self.mirror = os.path.join(self.mount, "hypixel", "sky-auctions-archive")
        self.receipts = os.path.join(self.tmp, "receipts")
        self.log = os.path.join(self.tmp, "calls.log")
        os.makedirs(self.bin)
        os.makedirs(self.receipts)
        for name, body in (("zfs", ZFS_STUB), ("zpool", ZPOOL_STUB)):
            p = os.path.join(self.bin, name)
            with open(p, "w") as fh:
                fh.write(body)
            os.chmod(p, 0o755)
        self.keys = []
        rows = []
        for i in range(12):
            key = "auctions/v2/T/2021-03/%03d.msgpack.br" % i
            data = (b"block%d" % i) * 50
            path = os.path.join(self.mirror, *key.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
            md5 = hashlib.md5(data).hexdigest()
            rows.append("%s\t%d\t%s\t%s\tok" % (key, len(data), md5, md5))
            self.keys.append((key, len(data), md5))
        with open(os.path.join(self.mirror, "_manifest-20261031T000000Z.tsv"), "w") as fh:
            fh.write("key\tsize\tetag\tmd5\tstatus\n" + "\n".join(rows) + "\n")
        receipt = {"version": 1, "month": "2021-03", "snapshot": "", "createdAt": "2026-10-31T00:00:00Z",
                   "tags": [{"tag": "T", "parts": 12, "rows": 1, "partSha256": [],
                             "objects": [{"key": k, "etag": e, "size": s} for k, s, e in self.keys]}]}
        self.receipt_path = os.path.join(self.receipts, "receipt-2021-03.json")
        with open(self.receipt_path, "w") as fh:
            json.dump(receipt, fh)

    def run_script(self, *extra, **env):
        e = dict(os.environ, PATH=self.bin + os.pathsep + os.environ["PATH"], STUB_LOG=self.log, STUB_MOUNT=self.mount)
        e.update(env)
        return subprocess.run(["bash", os.path.join(HERE, "snapshot.sh"), "--receipts", self.receipts, "--mirror", self.mirror,
                               *extra], capture_output=True, text=True, env=e)

    def calls(self):
        if not os.path.exists(self.log):
            return []
        with open(self.log) as fh:
            return fh.read().splitlines()

    def stamped(self):
        return glob.glob(os.path.join(self.receipts, "with-snapshot", "*.json"))

    def test_dry_run_changes_nothing(self):
        r = self.run_script()
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("DRY RUN", r.stdout)
        self.assertFalse([c for c in self.calls() if c.startswith(("zfs snapshot", "zfs hold"))])
        self.assertFalse(os.path.exists(os.path.join(self.mount, ".zfs")))
        self.assertEqual(self.stamped(), [])

    def test_unhealthy_pool_is_refused(self):
        r = self.run_script("--execute", STUB_ZPOOL="pool 'storage' is DEGRADED")
        self.assertNotEqual(r.returncode, 0)
        self.assertFalse([c for c in self.calls() if c.startswith("zfs snapshot")])

    def test_key_not_available_is_refused(self):
        r = self.run_script("--execute", STUB_KEYSTATUS="unavailable")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("key", r.stderr)
        self.assertFalse([c for c in self.calls() if c.startswith("zfs snapshot")])

    def test_not_mounted_is_refused(self):
        r = self.run_script("--execute", STUB_MOUNTED="no")
        self.assertNotEqual(r.returncode, 0)
        self.assertFalse([c for c in self.calls() if c.startswith("zfs snapshot")])

    def test_execute_snapshots_holds_verifies_and_stamps(self):
        r = self.run_script("--execute")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        calls = self.calls()
        snap = [c for c in calls if c.startswith("zfs snapshot")]
        hold = [c for c in calls if c.startswith("zfs hold")]
        self.assertEqual(len(snap), 1)
        self.assertRegex(snap[0], r"^zfs snapshot -r storage/backup@auction-archive-\d{8}T\d{4}Z$")
        self.assertEqual(len(hold), 1)
        self.assertRegex(hold[0], r"^zfs hold -r auction-archive storage/backup@auction-archive-\d{8}T\d{4}Z$")
        out = self.stamped()
        self.assertEqual(len(out), 1)
        with open(out[0]) as fh:
            doc = json.load(fh)
        self.assertRegex(doc["snapshot"], r"^auction-archive-\d{8}T\d{4}Z$")
        self.assertEqual(doc["version"], 1)
        with open(self.receipt_path) as fh:
            self.assertEqual(json.load(fh)["snapshot"], "", "original receipt untouched")

    def test_corrupt_file_in_snapshot_blocks_receipts(self):
        r = self.run_script("--execute", STUB_CORRUPT="hypixel/sky-auctions-archive/auctions/v2/T/2021-03/003.msgpack.br")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("md5 mismatch", r.stderr)
        self.assertEqual(self.stamped(), [])

    def test_missing_file_in_snapshot_blocks_receipts(self):
        r = self.run_script("--execute", STUB_DELETE="hypixel/sky-auctions-archive/auctions/v2/T/2021-03/007.msgpack.br")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("missing in snapshot", r.stderr)
        self.assertEqual(self.stamped(), [])

    def test_every_object_is_md5_checked_not_a_sample(self):
        # a corruption in each of the 12 files is found whichever one the (former) random sample would have skipped
        for i in range(12):
            with self.subTest(part=i):
                r = self.run_script("--execute", STUB_CORRUPT="hypixel/sky-auctions-archive/auctions/v2/T/2021-03/%03d.msgpack.br" % i)
                self.assertNotEqual(r.returncode, 0)
                self.assertIn("md5 mismatch", r.stderr)
                self.assertIn("auctions/v2/T/2021-03/%03d.msgpack.br" % i, r.stderr)
                self.assertEqual(self.stamped(), [])
                shutil.rmtree(os.path.join(self.mount, ".zfs"))

    def test_md5_is_compared_with_the_receipt_not_the_manifest(self):
        # the manifest still says the old md5, the receipt (what the retention worker trusts) says the mirror is different
        with open(self.receipt_path) as fh:
            doc = json.load(fh)
        doc["tags"][0]["objects"][4]["etag"] = '"%s"' % ("0" * 32)
        with open(self.receipt_path, "w") as fh:
            json.dump(doc, fh)
        r = self.run_script("--execute")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("md5 mismatch", r.stderr)
        self.assertEqual(self.stamped(), [])

    def test_multipart_etag_can_not_be_verified_and_fails(self):
        with open(self.receipt_path) as fh:
            doc = json.load(fh)
        doc["tags"][0]["objects"][0]["etag"] = "d41d8cd98f00b204e9800998ecf8427e-3"
        with open(self.receipt_path, "w") as fh:
            json.dump(doc, fh)
        r = self.run_script("--execute")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("can not be verified", r.stderr)
        self.assertEqual(self.stamped(), [])

    def test_only_the_given_receipts_are_stamped_other_receipts_stay_untouched(self):
        other = os.path.join(self.tmp, "older")
        os.makedirs(other)
        older = os.path.join(other, "receipt-2021-02.json")
        with open(older, "w") as fh:
            json.dump({"version": 1, "month": "2021-02", "snapshot": "", "tags": []}, fh)
        with open(older) as fh:
            before = fh.read()
        r = self.run_script("--execute")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        with open(older) as fh:
            self.assertEqual(fh.read(), before)
        self.assertEqual([os.path.basename(p) for p in self.stamped()], ["receipt-2021-03.json"])

    def test_object_without_size_or_key_fails_cleanly_and_stamps_nothing(self):
        for field in ("size", "key"):
            with self.subTest(missing=field):
                with open(self.receipt_path) as fh:
                    doc = json.load(fh)
                del doc["tags"][0]["objects"][3][field]
                with open(self.receipt_path, "w") as fh:
                    json.dump(doc, fh)
                r = self.run_script("--execute")
                self.assertNotEqual(r.returncode, 0)
                self.assertNotIn("Traceback", r.stderr)
                self.assertIn("snapshot verification FAILED", r.stderr)
                self.assertIn("an object has no " + ("valid size" if field == "size" else "key"), r.stderr)
                self.assertEqual(self.stamped(), [])
                shutil.rmtree(os.path.join(self.mount, ".zfs"), True)

    def test_malformed_tags_fail_cleanly_and_stamp_nothing(self):
        cases = {"tags not a list": ("tags", {"a": 1}), "tag not an object": ("tags", ["x"]), "tag without objects list": ("tags", [{"tag": "T", "objects": 5}])}
        for name, (field, value) in cases.items():
            with self.subTest(case=name):
                with open(self.receipt_path) as fh:
                    doc = json.load(fh)
                doc[field] = value
                with open(self.receipt_path, "w") as fh:
                    json.dump(doc, fh)
                r = self.run_script("--execute")
                self.assertNotEqual(r.returncode, 0)
                self.assertNotIn("Traceback", r.stderr)
                self.assertIn("ERROR ", r.stderr)
                self.assertIn("snapshot verification FAILED", r.stderr)
                self.assertEqual(self.stamped(), [])
                shutil.rmtree(os.path.join(self.mount, ".zfs"), True)

    def test_no_receipts_is_refused(self):
        os.remove(self.receipt_path)
        r = self.run_script("--execute")
        self.assertEqual(r.returncode, 2)
        self.assertFalse([c for c in self.calls() if c.startswith("zfs snapshot")])


if __name__ == "__main__":
    unittest.main()
