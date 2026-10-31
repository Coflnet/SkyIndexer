import hashlib
import http.server
import io
import os
import shutil
import stat
import subprocess
import tempfile
import threading
import unittest

import mirror

def slurp(path, mode="r"):
    with open(path, mode) as fh:
        return fh.read()


AK = "AKIAIOSFODNN7EXAMPLE"
SK = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"


class SigV4Tests(unittest.TestCase):
    # vectors from https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html
    def _sign(self, path, params, headers):
        h = {"host": "examplebucket.s3.amazonaws.com", "x-amz-content-sha256": mirror.EMPTY_SHA256,
             "x-amz-date": "20130524T000000Z"}
        h.update(headers)
        return mirror.sign_request("GET", path, params, h, mirror.EMPTY_SHA256, AK, SK, "20130524T000000Z", "us-east-1")

    def test_get_object_with_range(self):
        auth = self._sign("/test.txt", {}, {"range": "bytes=0-9"})
        self.assertIn("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", auth)
        self.assertTrue(auth.endswith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"), auth)
        self.assertIn("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request", auth)

    def test_get_lifecycle(self):
        auth = self._sign("/", {"lifecycle": ""}, {})
        self.assertTrue(auth.endswith("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543"), auth)

    def test_list_objects(self):
        auth = self._sign("/", {"max-keys": "2", "prefix": "J"}, {})
        self.assertTrue(auth.endswith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7"), auth)

    def test_signing_key_vector(self):
        # https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html
        key = mirror.signing_key("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "iam")
        self.assertEqual(key.hex(), "c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9")

    def test_uri_encoding(self):
        self.assertEqual(mirror.uri_encode("auctions/v2/A%2FB/x y", encode_slash=False), "auctions/v2/A%252FB/x%20y")
        self.assertEqual(mirror.canonical_query({"b": "a b", "a": "~"}), "a=~&b=a%20b")


class FakeClient:
    """In-memory R2: key -> bytes."""

    def __init__(self, objects, etags=None, corrupt_first=0):
        self.objects = objects
        self.etags = etags or {}
        self.gets = []
        self.corrupt = corrupt_first
        self.lock = threading.Lock()

    def etag(self, key):
        return self.etags.get(key) or hashlib.md5(self.objects[key]).hexdigest()

    def list(self, prefix):
        for k, v in self.objects.items():
            if k.startswith(prefix):
                yield k, len(v), self.etag(k)

    def get(self, key, fh):
        with self.lock:
            self.gets.append(key)
            corrupt = self.corrupt > 0
            if corrupt:
                self.corrupt -= 1
        data = self.objects[key]
        if corrupt:
            data = b"X" + data[1:]
        fh.write(data)
        return len(data), hashlib.md5(data).hexdigest(), self.etag(key)


class MirrorTestBase(unittest.TestCase):
    def setUp(self):
        self.dest = tempfile.mkdtemp(prefix="mirror-test-")
        self.addCleanup(shutil.rmtree, self.dest, True)
        self.logs = []

    def run_it(self, client, **kw):
        return mirror.run_mirror(client, self.dest, workers=4, log=self.logs.append, sleep=lambda s: None, **kw)

    def status(self, results):
        return {r.key: r.status for r in results}

    def path(self, key):
        return os.path.join(self.dest, *key.split("/"))


class MirrorTests(MirrorTestBase):
    OBJ = {"auctions/v2/A/2021-01/000.msgpack.br": b"alpha" * 10,
           "auctions/v2/A/index.msgpack.br": b"idx",
           "auctions/B/2020-01.json.gz": b"legacy"}

    def test_downloads_and_writes_manifest(self):
        c = FakeClient(dict(self.OBJ))
        results, manifest = self.run_it(c)
        self.assertEqual(set(self.status(results).values()), {"ok"})
        for k, v in self.OBJ.items():
            with open(self.path(k), "rb") as fh:
                self.assertEqual(fh.read(), v)
        lines = slurp(manifest).splitlines()
        self.assertEqual(lines[0], "key\tsize\tetag\tmd5\tstatus")
        self.assertEqual(len(lines), 4)
        self.assertEqual([r for r in os.listdir(self.dest) if r.endswith(mirror.TMP_SUFFIX)], [])

    def test_second_run_skips_everything(self):
        c = FakeClient(dict(self.OBJ))
        self.run_it(c)
        c.gets.clear()
        results, _ = self.run_it(c)
        self.assertEqual(c.gets, [])
        self.assertEqual(set(self.status(results).values()), {"skipped"})

    def test_reuses_old_mirror_files_without_manifest_md5(self):
        key = "auctions/v2/A/index.msgpack.br"
        os.makedirs(os.path.dirname(self.path(key)))
        with open(self.path(key), "wb") as fh:
            fh.write(b"idx")
        with open(os.path.join(self.dest, "_manifest-20261025T000000Z.tsv"), "w") as fh:
            fh.write("%s\t3\t%s\tok\n" % (key, hashlib.md5(b"idx").hexdigest()))
        c = FakeClient({key: b"idx"})
        results, _ = self.run_it(c)
        self.assertEqual(c.gets, [])
        self.assertEqual(self.status(results), {key: "skipped"})

    def test_changed_object_is_redownloaded(self):
        key = "auctions/v2/A/index.msgpack.br"
        c = FakeClient({key: b"old"})
        self.run_it(c)
        c.objects[key] = b"new-content"
        results, _ = self.run_it(c)
        self.assertEqual(self.status(results), {key: "ok"})
        self.assertEqual(slurp(self.path(key), "rb"), b"new-content")

    def test_corrupt_local_file_same_size_is_redownloaded(self):
        key = "auctions/v2/A/index.msgpack.br"
        c = FakeClient({key: b"good"})
        self.run_it(c)
        with open(self.path(key), "wb") as fh:
            fh.write(b"baad")
        results, _ = self.run_it(c)
        self.assertEqual(self.status(results), {key: "ok"})
        self.assertEqual(slurp(self.path(key), "rb"), b"good")

    def test_md5_mismatch_retries_then_succeeds(self):
        key = "auctions/v2/A/index.msgpack.br"
        c = FakeClient({key: b"payload"}, corrupt_first=2)
        results, _ = self.run_it(c)
        self.assertEqual(self.status(results), {key: "ok"})
        self.assertEqual(len(c.gets), 3)

    def test_persistent_corruption_fails_and_leaves_nothing(self):
        key = "auctions/v2/A/index.msgpack.br"
        c = FakeClient({key: b"payload"}, corrupt_first=99)
        results, manifest = self.run_it(c)
        self.assertTrue(results[0].failed)
        self.assertFalse(os.path.exists(self.path(key)))
        self.assertEqual([f for f in os.listdir(os.path.dirname(self.path(key))) if f.endswith(mirror.TMP_SUFFIX)], [])
        self.assertIn("failed", slurp(manifest))

    def test_never_deletes_local_files_gone_from_r2(self):
        key = "auctions/v2/A/000.msgpack.br"
        c = FakeClient({key: b"data"})
        self.run_it(c)
        del c.objects[key]
        c.objects["auctions/v2/A/other"] = b"x"
        results, manifest = self.run_it(c)
        self.assertTrue(os.path.exists(self.path(key)))
        self.assertEqual(self.status(results)[key], "gone")
        self.assertIn("\tgone", slurp(manifest))

    def test_multipart_etag_is_size_checked_and_trusts_manifest_only_when_equal(self):
        key = "auctions/v2/A/big"
        c = FakeClient({key: b"0123456789"}, etags={key: "abcdef-3"})
        results, _ = self.run_it(c)
        self.assertEqual(self.status(results), {key: "ok-multipart"})
        c.gets.clear()
        results, _ = self.run_it(c)
        self.assertEqual(c.gets, [])
        self.assertEqual(self.status(results), {key: "skipped-multipart"})
        c.etags[key] = "fedcba-3"  # changed object
        results, _ = self.run_it(c)
        self.assertEqual(len(c.gets), 1)

    def test_dry_run_downloads_nothing_and_writes_nothing(self):
        c = FakeClient(dict(self.OBJ))
        results, manifest = self.run_it(c, dry_run=True)
        self.assertIsNone(manifest)
        self.assertEqual(c.gets, [])
        self.assertEqual(os.listdir(self.dest), [])

    def test_missing_destination_is_refused(self):
        with self.assertRaises(mirror.MirrorError):
            mirror.run_mirror(FakeClient({}), os.path.join(self.dest, "nope"), log=lambda m: None)

    def test_stale_temp_files_are_removed(self):
        d = os.path.join(self.dest, "auctions", "v2", "A")
        os.makedirs(d)
        stale = os.path.join(d, "000.msgpack.br.1.2" + mirror.TMP_SUFFIX)
        open(stale, "w").close()
        self.run_it(FakeClient({}))
        self.assertFalse(os.path.exists(stale))

    def test_unsafe_keys_rejected(self):
        for k in ("auctions/../x", "other/x", "auctions//x", "/etc/passwd"):
            with self.assertRaises(mirror.MirrorError):
                mirror.safe_local_path(self.dest, k)

    def test_manifest_parsing_old_and_new_format(self):
        with open(os.path.join(self.dest, "_manifest-1.tsv"), "w") as fh:
            fh.write("auctions/a\t3\te1\tok\n")
        with open(os.path.join(self.dest, "_manifest-2.tsv"), "w") as fh:
            fh.write("key\tsize\tetag\tmd5\tstatus\nauctions/a\t4\te2\tm2\tok\nauctions/b\t1\te\tm\tgone\n")
        m = mirror.load_manifests(self.dest)
        self.assertEqual(m["auctions/a"], {"size": 4, "etag": "e2", "md5": "m2", "status": "ok"})
        self.assertEqual(m["auctions/b"]["status"], "gone")


class CredentialTests(MirrorTestBase):
    def write_env(self, mode, text):
        p = os.path.join(self.dest, "r2.env")
        with open(p, "w") as fh:
            fh.write(text)
        os.chmod(p, mode)
        return p

    GOOD = "# c\nR2_ENDPOINT=https://x.r2.cloudflarestorage.com\nR2_ACCESS_KEY_ID='abc12345'\nexport R2_SECRET_ACCESS_KEY=\"sek-ret-value\"\nR2_BUCKET=b\n"

    def test_loads_and_defaults_region(self):
        c = mirror.load_credentials(self.write_env(0o600, self.GOOD))
        self.assertEqual(c["R2_ACCESS_KEY_ID"], "abc12345")
        self.assertEqual(c["R2_SECRET_ACCESS_KEY"], "sek-ret-value")
        self.assertEqual(c["R2_REGION"], "auto")

    def test_world_readable_refused_without_leaking(self):
        with self.assertRaises(mirror.MirrorError) as cm:
            mirror.load_credentials(self.write_env(0o644, self.GOOD))
        self.assertNotIn("sek-ret-value", str(cm.exception))

    def test_missing_key_message_has_no_values(self):
        with self.assertRaises(mirror.MirrorError) as cm:
            mirror.load_credentials(self.write_env(0o600, "R2_BUCKET=b\nR2_ACCESS_KEY_ID=zzzzzzzz\n"))
        self.assertIn("R2_ENDPOINT", str(cm.exception))
        self.assertNotIn("zzzzzzzz", str(cm.exception))

    def test_redactor(self):
        r = mirror.Redactor(["abc12345", "sek-ret-value"])
        self.assertEqual(r("key abc12345 secret sek-ret-value"), "key *** secret ***")

    def test_main_refuses_when_zfs_key_not_loaded(self):
        env = self.write_env(0o600, self.GOOD)
        def fake_run(cmd, **kw):
            return subprocess.CompletedProcess(cmd, 0, "keystatus\tunavailable\nmounted\tyes\n", "")
        with self.assertRaises(mirror.MirrorError):
            mirror.check_zfs("storage/backup", run=fake_run)
        ok = lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "keystatus\tavailable\nmounted\tyes\n", "")
        mirror.check_zfs("storage/backup", run=ok)
        notmounted = lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "keystatus\tavailable\nmounted\tno\n", "")
        with self.assertRaises(mirror.MirrorError):
            mirror.check_zfs("storage/backup", run=notmounted)


class HttpFixture(http.server.BaseHTTPRequestHandler):
    objects = {}
    seen_auth = []

    def log_message(self, *a):
        pass

    def do_GET(self):
        import urllib.parse
        u = urllib.parse.urlsplit(self.path)
        HttpFixture.seen_auth.append(self.headers.get("Authorization", ""))
        q = urllib.parse.parse_qs(u.query)
        if "list-type" in q:
            keys = sorted(k for k in self.objects if k.startswith(q["prefix"][0]))
            start = int(q.get("continuation-token", ["0"])[0])
            page = keys[start:start + 2]
            more = start + 2 < len(keys)
            body = "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>%s</IsTruncated>" % str(more).lower()
            if more:
                body += "<NextContinuationToken>%d</NextContinuationToken>" % (start + 2)
            for k in page:
                body += "<Contents><Key>%s</Key><Size>%d</Size><ETag>&quot;%s&quot;</ETag></Contents>" % (
                    urllib.parse.quote(k), len(self.objects[k]), hashlib.md5(self.objects[k]).hexdigest())
            body += "</ListBucketResult>"
            data = body.encode()
        else:
            key = urllib.parse.unquote(u.path).split("/", 2)[2]
            if key not in self.objects:
                self.send_response(404)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            data = self.objects[key]
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class S3ClientHttpTests(MirrorTestBase):
    def test_list_paginates_and_downloads_over_http(self):
        HttpFixture.objects = {"auctions/v2/A%2FB/000.msgpack.br": b"one", "auctions/v2/x y/001": b"two",
                               "auctions/v2/C/002": b"three", "other/ignored": b"no"}
        srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), HttpFixture)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        client = mirror.S3Client("http://127.0.0.1:%d" % srv.server_address[1], "bkt", AK, SK)
        results, _ = self.run_it(client)
        self.assertEqual(len(results), 3)
        self.assertEqual(set(r.status for r in results), {"ok"})
        self.assertEqual(slurp(self.path("auctions/v2/A%2FB/000.msgpack.br"), "rb"), b"one")
        self.assertTrue(all(a.startswith("AWS4-HMAC-SHA256 Credential=" + AK) for a in HttpFixture.seen_auth))

    def test_http_404_is_failed_not_retried_forever(self):
        HttpFixture.objects = {}
        srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), HttpFixture)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        client = mirror.S3Client("http://127.0.0.1:%d" % srv.server_address[1], "bkt", AK, SK)
        r = mirror.process_object(client, self.dest, "auctions/v2/q", 3, "x" * 32, None, sleep=lambda s: None)
        self.assertTrue(r.failed)
        self.assertNotIn(SK, r.status)


if __name__ == "__main__":
    unittest.main()
