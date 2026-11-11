import hashlib
import http.client
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

import mirror
import upload_receipts as ur

SNAP = "auction-archive-20261101T0300Z"
SECRET = "sUpErSeCrEtValue1234567890"
ACCESS = "AKIAEXAMPLEACCESS99"


def receipt(month="2021-03", snapshot=SNAP, **over):
    r = {"version": 1, "month": month, "snapshot": snapshot, "createdAt": "2026-11-01T03:00:00Z",
         "tags": [{"tag": "HYPERION", "parts": 1, "rows": 5, "partSha256": ["aa"],
                   "objects": [{"key": "auctions/v2/HYPERION/%s/000.msgpack.br" % month, "etag": "abc", "size": 12}]}]}
    r.update(over)
    return r


def hold_ok(argv):
    return 0, "storage/backup@%s\tauction-archive\tMon Nov  1  3:00 2026\n" % argv[-1].split("@")[1]


class FakeStore:
    def __init__(self, objects=None, corrupt_readback=False, fail_put=None, get_error=None):
        self.get_error = get_error
        self.objects = dict(objects or {})
        self.puts = []
        self.corrupt = corrupt_readback
        self.fail_put = fail_put

    def get_bytes(self, key):
        if self.get_error:
            raise mirror.MirrorError(self.get_error)
        data = self.objects.get(key)
        return data + b"x" if data is not None and self.corrupt and key in dict(self.puts) else data

    def put_bytes(self, key, data):
        if self.fail_put:
            raise mirror.MirrorError(self.fail_put)
        self.puts.append((key, data))
        self.objects[key] = data


class UploadTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.dir = os.path.join(self.tmp, "with-snapshot")
        os.mkdir(self.dir)
        self.env = os.path.join(self.tmp, "rw.env")
        with open(self.env, "w") as fh:
            fh.write("R2_ENDPOINT=https://x.r2.example\nR2_ACCESS_KEY_ID=%s\nR2_SECRET_ACCESS_KEY=%s\nR2_BUCKET=b\n" % (ACCESS, SECRET))
        os.chmod(self.env, 0o600)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, month, content=None, name=None):
        content = receipt(month) if content is None else content
        raw = content if isinstance(content, (bytes, str)) else json.dumps(content)
        with open(os.path.join(self.dir, name or "receipt-%s.json" % month), "wb") as fh:
            fh.write(raw if isinstance(raw, bytes) else raw.encode())

    def run_main(self, store, *extra, execute=False, runner=None, factory=None):
        argv = ["--receipts", self.dir, "--env-file", self.env] + list(extra) + (["--execute"] if execute else [])
        out = io.StringIO()
        factory = factory or (lambda creds: (store, mirror.Redactor([ACCESS, SECRET])))
        code = ur.main(argv, store_factory=factory, out=out, runner=runner or hold_ok)
        return code, out.getvalue()

    def assert_refused(self, store=None):
        store = store or FakeStore()
        code, text = self.run_main(store, execute=True)
        self.assertEqual(code, 2, text)
        self.assertEqual(store.puts, [])
        return text

    def test_unstamped_refused(self):
        self.write("2021-03", receipt(snapshot=""))
        self.assertIn("snapshot", self.assert_refused())

    def test_bad_snapshot_name_refused(self):
        self.write("2021-03", receipt(snapshot="auction-archive-2026"))
        self.assert_refused()

    def test_wrong_version_refused(self):
        self.write("2021-03", receipt(version=2))
        self.assertIn("version", self.assert_refused())

    def test_month_mismatch_refused(self):
        self.write("2021-03", receipt(month="2021-04"))
        self.assertIn("month", self.assert_refused())

    def test_bad_file_name_refused(self):
        self.write("2021-03", name="receipt-2021-13.json")
        self.assertIn("file name", self.assert_refused())

    def test_invalid_json_refused(self):
        self.write("2021-03", b"{nope")
        self.assert_refused()

    def test_empty_tags_or_bad_objects_refused(self):
        self.write("2021-03", receipt(tags=[]))
        self.assert_refused()
        bad = receipt()
        del bad["tags"][0]["objects"][0]["etag"]
        self.write("2021-03", bad)
        self.assert_refused()
        bad = receipt()
        bad["tags"][0]["objects"][0]["size"] = "12"
        self.write("2021-03", bad)
        self.assert_refused()

    def test_mixed_snapshots_refused_even_when_filtered(self):
        self.write("2021-03")
        self.write("2021-04", receipt("2021-04", snapshot="auction-archive-20261102T0300Z"))
        store = FakeStore()
        code, text = self.run_main(store, "--month", "2021-03", execute=True)
        self.assertEqual(code, 2)
        self.assertIn("different snapshots", text)
        self.assertEqual(store.puts, [])

    def test_one_bad_file_blocks_the_good_ones(self):
        self.write("2021-03")
        self.write("2021-04", receipt("2021-04", version=0))
        self.assert_refused()

    def test_missing_month_filter_refused(self):
        self.write("2021-03")
        code, _ = self.run_main(FakeStore(), "--month", "2021-05", execute=True)
        self.assertEqual(code, 2)

    def test_missing_env_file_refused(self):
        self.write("2021-03")
        os.remove(self.env)
        self.assert_refused()

    def test_dry_run_makes_no_put(self):
        self.write("2021-03")
        store = FakeStore()
        code, text = self.run_main(store)
        self.assertEqual(code, 0, text)
        self.assertEqual(store.puts, [])
        self.assertIn("would be new", text)

    def test_execute_new_then_unchanged_skips_put(self):
        self.write("2021-03")
        store = FakeStore()
        code, text = self.run_main(store, execute=True)
        self.assertEqual(code, 0, text)
        self.assertEqual([k for k, _ in store.puts], ["auctions/v2/_meta/backup/2021-03.json"])
        self.assertIn("new", text)
        code, text = self.run_main(store, execute=True)
        self.assertEqual(code, 0)
        self.assertEqual(len(store.puts), 1)
        self.assertIn("unchanged", text)

    def test_replaced_when_existing_differs(self):
        self.write("2021-03")
        store = FakeStore({"auctions/v2/_meta/backup/2021-03.json": b"old"})
        code, text = self.run_main(store, execute=True)
        self.assertEqual(code, 0)
        self.assertIn("replaced", text)
        self.assertEqual(len(store.puts), 1)

    def test_month_filter_uploads_only_selected(self):
        self.write("2021-03")
        self.write("2021-04", receipt("2021-04"))
        store = FakeStore()
        self.run_main(store, "--month", "2021-04", execute=True)
        self.assertEqual([k for k, _ in store.puts], ["auctions/v2/_meta/backup/2021-04.json"])

    def test_read_back_mismatch_exits_1(self):
        self.write("2021-03")
        code, text = self.run_main(FakeStore(corrupt_readback=True), execute=True)
        self.assertEqual(code, 1)
        self.assertIn("READ-BACK MISMATCH", text)

    def test_put_failure_exits_1_and_secrets_not_printed(self):
        self.write("2021-03")
        code, text = self.run_main(FakeStore(fail_put="boom " + SECRET + " " + ACCESS), execute=True)
        self.assertEqual(code, 1)
        self.assertNotIn(SECRET, text)
        self.assertNotIn(ACCESS, text)
        self.assertIn("***", text)

    def test_unexpected_exception_does_not_leak(self):
        self.write("2021-03")

        class Exploding(FakeStore):
            def get_bytes(self, key):
                raise RuntimeError("secret is " + SECRET)

        code, text = self.run_main(Exploding(), execute=True)
        self.assertEqual(code, 1)
        self.assertNotIn(SECRET, text)

    def test_secrets_never_in_normal_output(self):
        self.write("2021-03")
        _, text = self.run_main(FakeStore(), execute=True)
        self.assertNotIn(SECRET, text)
        self.assertNotIn(ACCESS, text)

    def test_wide_open_env_file_refused(self):
        self.write("2021-03")
        os.chmod(self.env, 0o644)
        self.assert_refused()

    # ---- review fixes

    def test_runs_under_isolated_mode(self):
        script = os.path.join(os.path.dirname(os.path.abspath(ur.__file__)), "upload_receipts.py")
        done = subprocess.run([sys.executable, "-I", script, "--help"], capture_output=True, text=True, cwd=self.tmp)
        self.assertEqual(done.returncode, 0, done.stderr)

    def test_bad_endpoint_is_refused_not_traceback(self):
        self.write("2021-03")
        with open(self.env, "w") as fh:
            fh.write("R2_ENDPOINT=ftp://x\nR2_ACCESS_KEY_ID=%s\nR2_SECRET_ACCESS_KEY=%s\nR2_BUCKET=b\n" % (ACCESS, SECRET))
        code, text = self.run_main(None, execute=True, factory=ur.make_store)
        self.assertEqual(code, 2, text)
        self.assertIn("refused", text)

    def test_filtered_run_still_validates_other_files(self):
        self.write("2021-03")
        self.write("2021-04", b"{broken")
        code, _ = self.run_main(FakeStore(), "--month", "2021-03", execute=True)
        self.assertEqual(code, 2)

    def test_only_no_such_key_404_is_missing(self):
        store = ur.R2Store("http://127.0.0.1:1", "b", ACCESS, SECRET)
        store._send = lambda *a, **k: (404, b"<Error><Code>NoSuchKey</Code></Error>")
        self.assertIsNone(store.get_bytes("k"))
        store._send = lambda *a, **k: (404, b"<Error><Code>NoSuchBucket</Code></Error>")
        with self.assertRaises(mirror.MirrorError):
            store.get_bytes("k")

    def test_get_error_in_dry_run_exits_1(self):
        self.write("2021-03")
        code, text = self.run_main(FakeStore(get_error="HTTP 404 NoSuchBucket"))
        self.assertEqual(code, 1)
        self.assertIn("FAILED", text)

    def test_newer_remote_snapshot_refused_before_any_put(self):
        self.write("2021-03")
        self.write("2021-04", receipt("2021-04"))
        newer = json.dumps(receipt("2021-04", snapshot="auction-archive-20261201T0300Z")).encode()
        store = FakeStore({"auctions/v2/_meta/backup/2021-04.json": newer})
        code, text = self.run_main(store, execute=True)
        self.assertEqual(code, 2, text)
        self.assertEqual(store.puts, [])
        self.assertIn("NEWER", text)
        code, _ = self.run_main(store, "--allow-older", execute=True)
        self.assertEqual(code, 0)
        self.assertEqual(len(store.puts), 2)

    def test_replaced_prints_old_and_new_snapshot(self):
        self.write("2021-03")
        older = json.dumps(receipt(snapshot="auction-archive-20260101T0300Z")).encode()
        _, text = self.run_main(FakeStore({"auctions/v2/_meta/backup/2021-03.json": older}), execute=True)
        self.assertIn("auction-archive-20260101T0300Z -> " + SNAP, text)

    def test_missing_hold_or_failing_zfs_refused_also_in_dry_run(self):
        self.write("2021-03")
        for runner in (lambda argv: (0, "storage/backup@x\tother-tag\tt\n"), lambda argv: (0, ""),
                       lambda argv: (1, "")):
            store = FakeStore()
            code, text = self.run_main(store, runner=runner)
            self.assertEqual(code, 2, text)

    def test_hold_command_uses_dataset_and_tag(self):
        self.write("2021-03")
        calls = []

        def runner(argv):
            calls.append(argv)
            return 0, "n\tmy-tag\tt\n"

        code, _ = self.run_main(FakeStore(), "--dataset", "tank/x", "--hold-tag", "my-tag", runner=runner)
        self.assertEqual(code, 0)
        self.assertEqual(calls, [["zfs", "holds", "-H", "tank/x@" + SNAP]])

    def test_trailing_newline_in_names_refused(self):
        self.write("2021-03", receipt(snapshot=SNAP + "\n"))
        self.assert_refused()
        self.write("2021-03", receipt(month="2021-03\n"))
        self.assert_refused()

    def test_per_tag_checks(self):
        bad = receipt()
        bad["tags"][0]["partSha256"] = []
        self.write("2021-03", bad)
        self.assert_refused()
        dup = receipt()
        dup["tags"].append(dict(dup["tags"][0]))
        self.write("2021-03", dup)
        self.assert_refused()
        self.write("2021-03", receipt(createdAt="yesterday"))
        self.assert_refused()
        self.write("2021-03", receipt(createdAt="2026-10-31T14:00:00.0000000Z"))
        code, _ = self.run_main(FakeStore(), execute=True)
        self.assertEqual(code, 0)


TOKEN = "cfat_Zx9QpLmN3vTrYb7KdWe2HsJc5GfUa8Io1XtRn4EyVq0"
TOKEN_ID = "0123456789abcdef0123456789abcdef"
ACCOUNT = "a1b2c3d4e5f60718293a4b5c6d7e8f90"
ENDPOINT = "https://%s.r2.cloudflarestorage.com" % ACCOUNT


def verify_ok(status="active", success=True):
    body = json.dumps({"success": success, "errors": [], "result": {"id": TOKEN_ID, "status": status}}).encode()
    return lambda url, token: (200, body)


class TokenCredentialTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.dir = os.path.join(self.tmp, "with-snapshot")
        os.mkdir(self.dir)
        with open(os.path.join(self.dir, "receipt-2021-03.json"), "w") as fh:
            json.dump(receipt("2021-03"), fh)
        self.env = self.file("rw.env", TOKEN + "\n")
        self.ro = self.file("ro.env", "R2_ENDPOINT=%s\nR2_ACCESS_KEY_ID=ro\nR2_SECRET_ACCESS_KEY=rosecret\n"
                                      "R2_BUCKET=sky-bucket\nR2_REGION=eeur\n" % ENDPOINT)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def file(self, name, content, mode=0o600):
        path = os.path.join(self.tmp, name)
        with open(path, "w") as fh:
            fh.write(content)
        os.chmod(path, mode)
        return path

    def creds(self, http_get=None, env=None):
        return ur.load_write_credentials(env or self.env, self.ro, http_get or verify_ok())

    def main(self, http_get, *extra):
        out, seen = io.StringIO(), []

        def factory(creds):
            seen.append(creds)
            return FakeStore(), mirror.Redactor([TOKEN, TOKEN_ID])
        argv = ["--receipts", self.dir, "--env-file", self.env, "--endpoint-env-file", self.ro] + list(extra)
        code = ur.main(argv, store_factory=factory, out=out, runner=hold_ok, http_get=http_get)
        return code, out.getvalue(), seen

    def test_bare_token_file(self):
        c = self.creds()
        self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)
        self.assertEqual(c["R2_SECRET_ACCESS_KEY"], hashlib.sha256(TOKEN.encode()).hexdigest())
        self.assertEqual((c["R2_ENDPOINT"], c["R2_BUCKET"], c["R2_REGION"]), (ENDPOINT, "sky-bucket", "eeur"))

    def test_bare_token_with_comments_and_blank_lines(self):
        c = self.creds(env=self.file("c.env", "# note\n\n  %s  \n" % TOKEN))
        self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)

    def test_keyed_token_files(self):
        for key in ("R2_API_TOKEN", "CLOUDFLARE_API_TOKEN"):
            c = self.creds(env=self.file("k.env", "%s=%s\n" % (key, TOKEN)))
            self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)

    def test_endpoint_in_token_file_is_preferred(self):
        other = "https://%s.eu.r2.cloudflarestorage.com" % ("f" * 32)
        env = self.file("k.env", "R2_API_TOKEN=%s\nR2_ENDPOINT=%s\nR2_BUCKET=mine\n" % (TOKEN, other))
        urls = []
        c = self.creds(http_get=lambda u, t: (urls.append(u), verify_ok()(u, t))[1], env=env)
        self.assertEqual((c["R2_ENDPOINT"], c["R2_BUCKET"]), (other, "mine"))
        self.assertIn("f" * 32, urls[0])

    def test_full_four_key_file_unchanged_and_no_http(self):
        env = self.file("full.env", "R2_ENDPOINT=https://x.r2.example\nR2_ACCESS_KEY_ID=%s\nR2_SECRET_ACCESS_KEY=%s\nR2_BUCKET=b\n" % (ACCESS, SECRET))

        def boom(url, token):
            raise AssertionError("no http expected")
        c = self.creds(http_get=boom, env=env)
        self.assertEqual((c["R2_ACCESS_KEY_ID"], c["R2_SECRET_ACCESS_KEY"], c["R2_REGION"]), (ACCESS, SECRET, "auto"))

    def test_cfat_token_only_uses_account_verify_url(self):
        urls = []
        self.creds(http_get=lambda u, t: (urls.append(u), verify_ok()(u, t))[1])
        self.assertEqual(urls, ["https://api.cloudflare.com/client/v4/accounts/%s/tokens/verify" % ACCOUNT])

    def test_user_token_tries_user_then_account(self):
        urls = []
        bad = json.dumps({"success": False, "errors": [{"message": "Invalid"}]}).encode()

        def get(url, token):
            urls.append(url)
            return (401, bad) if "/user/" in url else verify_ok()(url, token)
        c = self.creds(http_get=get, env=self.file("u.env", "plainusertoken1234567890abcdef\n"))
        self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)
        self.assertEqual(len(urls), 2)
        self.assertIn("/user/tokens/verify", urls[0])

    def test_account_id_parsing(self):
        self.assertEqual(ur.account_id_of(ENDPOINT), ACCOUNT)
        self.assertEqual(ur.account_id_of("https://%s.eu.r2.cloudflarestorage.com/" % ACCOUNT), ACCOUNT)
        for bad in ("https://x.r2.example", "https://abc.r2.cloudflarestorage.com",
                    "https://evil.com/%s.r2.cloudflarestorage.com" % ACCOUNT,
                    "https://%s.r2.cloudflarestorage.com.evil.com" % ACCOUNT):
            with self.assertRaises(ur.Refused, msg=bad):
                ur.account_id_of(bad)

    def test_bad_endpoint_host_refused_exit_2(self):
        self.file("ro.env", "R2_ENDPOINT=https://x.r2.example\nR2_BUCKET=b\n")
        code, text, _ = self.main(verify_ok())
        self.assertEqual(code, 2, text)
        self.assertIn("account id", text)
        self.assertNotIn(TOKEN, text)

    def test_missing_endpoint_file_refused(self):
        os.remove(self.ro)
        code, text, _ = self.main(verify_ok())
        self.assertEqual(code, 2, text)

    def test_failed_verify_refused_and_token_not_printed(self):
        body = json.dumps({"success": False, "errors": [{"code": 1000, "message": "bad token " + TOKEN}]}).encode()
        code, text, seen = self.main(lambda u, t: (403, body))
        self.assertEqual(code, 2, text)
        self.assertNotIn(TOKEN, text)
        self.assertIn("verify failed", text)
        self.assertEqual(seen, [])

    def test_inactive_token_refused(self):
        code, text, _ = self.main(verify_ok(status="expired"))
        self.assertEqual(code, 2, text)
        self.assertIn("not active", text)

    def test_success_false_with_result_refused(self):
        code, _, _ = self.main(verify_ok(success=False))
        self.assertEqual(code, 2)

    def test_non_json_response_refused(self):
        code, text, _ = self.main(lambda u, t: (502, b"<html>" + TOKEN.encode() + b"</html>"))
        self.assertEqual(code, 2, text)
        self.assertNotIn(TOKEN, text)

    def test_network_error_refused(self):
        def down(url, token):
            raise ur.Refused("token verify request failed: URLError")
        code, text, _ = self.main(down)
        self.assertEqual(code, 2, text)
        self.assertNotIn(TOKEN, text)

    def test_wide_open_token_file_refused(self):
        os.chmod(self.env, 0o644)
        code, text, _ = self.main(verify_ok())
        self.assertEqual(code, 2, text)
        self.assertNotIn(TOKEN, text)

    def test_file_without_token_refused(self):
        env = self.file("n.env", "R2_BUCKET=b\nOTHER=1\n")
        with self.assertRaises(ur.Refused):
            self.creds(env=env)

    def test_success_path_passes_derived_creds_and_prints_no_secret(self):
        code, text, seen = self.main(verify_ok())
        self.assertEqual(code, 0, text)
        c = seen[0]
        self.assertEqual(c["R2_SECRET_ACCESS_KEY"], hashlib.sha256(TOKEN.encode()).hexdigest())
        for secret in (TOKEN, TOKEN_ID, c["R2_SECRET_ACCESS_KEY"]):
            self.assertNotIn(secret, text)

    def test_bom_token_file_works(self):
        c = self.creds(env=self.file("b.env", "\ufeff" + TOKEN + "\n"))
        self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)
        c = self.creds(env=self.file("b2.env", "\ufeffR2_API_TOKEN=%s\n" % TOKEN))
        self.assertEqual(c["R2_ACCESS_KEY_ID"], TOKEN_ID)

    def test_malformed_tokens_refused_without_echo(self):
        for bad in ("t\u00f6ken_with_non_ascii_chars_1234", "short", "has.dots.in.the.token.value.1234"):
            code, text, _ = self.main_with_env(bad + "\n")
            self.assertEqual(code, 2, text)
            self.assertNotIn(bad, text)
            self.assertIn("unexpected format", text)

    def main_with_env(self, content):
        self.env = self.file("m.env", content)
        return self.main(verify_ok())

    def test_token_id_must_be_32_hex(self):
        for bad in ("short", "G" * 32, "A" * 32):
            body = json.dumps({"success": True, "result": {"id": bad, "status": "active"}}).encode()
            code, text, _ = self.main(lambda u, t: (200, body))
            self.assertEqual(code, 2, text)

    def test_http_endpoint_refused_in_token_mode(self):
        self.file("ro.env", "R2_ENDPOINT=http://%s.r2.cloudflarestorage.com\nR2_BUCKET=b\n" % ACCOUNT)
        code, text, _ = self.main(verify_ok())
        self.assertEqual(code, 2, text)
        self.assertIn("https", text)

    def test_unexpected_exception_while_preparing_prints_no_traceback(self):
        def explode(url, token):
            raise RuntimeError(TOKEN)
        code, text, _ = self.main(explode)
        self.assertEqual(code, 2, text)
        self.assertNotIn(TOKEN, text)
        self.assertIn("RuntimeError", text)

    def test_make_store_redacts_token_secret_and_id(self):
        store, redact = ur.make_store(self.creds())
        text = redact("%s %s %s" % (TOKEN, TOKEN_ID, hashlib.sha256(TOKEN.encode()).hexdigest()))
        self.assertEqual(text.replace("***", "").strip(), "")
        self.assertEqual(store.access_key, TOKEN_ID)


class FakeConn:
    def __init__(self, status=200, body=b"{}", fail=None, read_fail=None):
        self.status, self.body, self.fail, self.read_fail = status, body, fail, read_fail
        self.requests, self.closed = [], False

    def request(self, method, path, headers=None):
        if self.fail:
            raise self.fail
        self.requests.append((method, path, headers))

    def getresponse(self):
        conn = self

        class Resp:
            status = conn.status

            def read(self):
                if conn.read_fail:
                    raise conn.read_fail
                return conn.body
        return Resp()

    def close(self):
        self.closed = True


class CloudflareGetTests(unittest.TestCase):
    URL = "https://api.cloudflare.com/client/v4/accounts/%s/tokens/verify" % ACCOUNT

    def get(self, conn, url=None):
        real = ur.open_cloudflare_connection
        ur.open_cloudflare_connection = lambda: conn
        try:
            return ur.cloudflare_get(url or self.URL, TOKEN)
        finally:
            ur.open_cloudflare_connection = real

    def test_sends_bearer_to_path_and_returns_status_body(self):
        conn = FakeConn(200, b'{"a":1}')
        self.assertEqual(self.get(conn), (200, b'{"a":1}'))
        method, path, headers = conn.requests[0]
        self.assertEqual((method, path), ("GET", "/client/v4/accounts/%s/tokens/verify" % ACCOUNT))
        self.assertEqual(headers["Authorization"], "Bearer " + TOKEN)
        self.assertTrue(conn.closed)

    def test_error_status_returns_code_and_body(self):
        self.assertEqual(self.get(FakeConn(403, b"nope")), (403, b"nope"))

    def test_network_and_read_errors_refused_without_token(self):
        for conn in (FakeConn(fail=OSError(TOKEN)), FakeConn(read_fail=http.client.IncompleteRead(b"x")),
                     FakeConn(read_fail=ConnectionResetError(TOKEN))):
            with self.assertRaises(ur.Refused) as cm:
                self.get(conn)
            self.assertNotIn(TOKEN, str(cm.exception))

    def test_redirect_refused(self):
        for status in (301, 302, 307, 308):
            with self.assertRaises(ur.Refused) as cm:
                self.get(FakeConn(status))
            self.assertIn("redirect", str(cm.exception))

    def test_token_only_goes_to_cloudflare_host(self):
        for url in ("http://api.cloudflare.com/x", "https://evil.example/x", "https://api.cloudflare.com.evil.example/x"):
            conn = FakeConn()
            with self.assertRaises(ur.Refused):
                self.get(conn, url)
            self.assertEqual(conn.requests, [])

    def test_default_connection_is_tls_to_cloudflare(self):
        conn = ur.open_cloudflare_connection()
        self.assertIsInstance(conn, http.client.HTTPSConnection)
        self.assertEqual(conn.host, "api.cloudflare.com")
        self.assertEqual(conn.timeout, 20)


if __name__ == "__main__":
    unittest.main()
