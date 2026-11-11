#!/usr/bin/env python3
"""Upload the stamped backup receipts (snapshot.sh output) to R2 so SkyIndexer's retention worker can see them.

    python3 -I upload_receipts.py --receipts <out dir>/with-snapshot            # dry run: validate + plan
    python3 -I upload_receipts.py --receipts <out dir>/with-snapshot --execute  # PUT + read back

Each ``receipt-yyyy-MM.json`` goes to ``auctions/v2/_meta/backup/yyyy-MM.json`` (Content-Type application/json,
Content-MD5), is read back and compared byte for byte. Per month the result is ``new``, ``replaced`` (an object with
different bytes existed) or ``unchanged`` (identical bytes already there, no PUT).

Credentials: env file (default ~/.config/coflnet/sky-auctions-r2-rw.env, mode 600) with the keys documented in
mirror.py (R2_ENDPOINT, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET, optional R2_REGION) but a token that may
write. Alternatively the file may hold a Cloudflare API token (bare on one line, or R2_API_TOKEN= /
CLOUDFLARE_API_TOKEN=); then endpoint and bucket come from the file itself or --endpoint-env-file (default the ro env
file), the token id is looked up via the Cloudflare token verify API (access key id) and sha256(token) is the secret.
Secrets are never printed (error text is redacted, no tracebacks).

Everything is validated before the first upload; one bad file refuses the whole run.
Safety: refuses a month whose R2 receipt names a NEWER snapshot (unless --allow-older) and requires the snapshot to
still exist with the hold tag (``zfs holds``). Exit code 0 = ok, 1 = at least one month failed, 2 = refused / bad configuration.
"""
import argparse
import base64
import collections
import datetime
import hashlib
import http.client
import json
import os
import re
import ssl
import stat
import subprocess
import sys
import urllib.parse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python3 -I drops the script dir
import mirror  # noqa: E402
from mirror import MirrorError, Redactor, S3Client

DEFAULT_ENV_FILE = "~/.config/coflnet/sky-auctions-r2-rw.env"
DEFAULT_ENDPOINT_ENV_FILE = "~/.config/coflnet/sky-auctions-r2-ro.env"
TOKEN_KEYS = ("R2_API_TOKEN", "CLOUDFLARE_API_TOKEN")
CF_API = "https://api.cloudflare.com/client/v4"
CF_TIMEOUT = 20
CF_HOST = "api.cloudflare.com"
TOKEN_RE = re.compile(r"[A-Za-z0-9_-]{20,}")
TOKEN_ID_RE = re.compile(r"[0-9a-f]{32}")
ENDPOINT_HOST_RE = re.compile(r"([0-9a-f]{32})\.(?:[a-z]+\.)?r2\.cloudflarestorage\.com")
META_KEY = "auctions/v2/_meta/backup/%s.json"
FILE_RE = re.compile(r"receipt-(\d{4}-(?:0[1-9]|1[0-2]))\.json")
SNAPSHOT_RE = re.compile(r"auction-archive-(\d{8}T\d{4}Z)")
MONTH_RE = re.compile(r"\d{4}-(?:0[1-9]|1[0-2])")
TIME_RE = re.compile(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.\d+)?(Z|[+-]\d\d:\d\d)")
DEFAULT_DATASET = "storage/backup"
DEFAULT_HOLD_TAG = "auction-archive"


class Refused(Exception):
    """Validation / configuration problem; the message is safe to print."""


# --------------------------------------------------------------------------- validation

Item = collections.namedtuple("Item", "month key data snapshot")


def is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def check_time(value, where):
    match = TIME_RE.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise Refused("%s: createdAt is not ISO-8601" % where)
    zone = "+00:00" if match.group(3) == "Z" else match.group(3)
    try:
        datetime.datetime.fromisoformat(match.group(1) + (match.group(2) or "")[:7] + zone)
    except ValueError:
        raise Refused("%s: createdAt is not a valid date" % where)


def check_object(obj, where):
    if not isinstance(obj, dict):
        raise Refused("%s: object entry is not a JSON object" % where)
    for name in ("key", "etag"):
        if not isinstance(obj.get(name), str) or not obj[name]:
            raise Refused("%s: object without %s" % (where, name))
    if not is_int(obj.get("size")) or obj["size"] < 0:
        raise Refused("%s: object %s without valid size" % (where, obj["key"]))


def check_tag(tag, where):
    if not isinstance(tag, dict) or not isinstance(tag.get("tag"), str) or not tag["tag"]:
        raise Refused("%s: tag entry without name" % where)
    where = "%s tag %s" % (where, tag["tag"])
    objects = tag.get("objects")
    if not isinstance(objects, list) or not objects:
        raise Refused("%s: no objects" % where)
    for obj in objects:
        check_object(obj, where)
    hashes = tag.get("partSha256")
    if not is_int(tag.get("parts")) or not isinstance(hashes, list) or len(hashes) != tag["parts"]:
        raise Refused("%s: partSha256 length does not equal parts" % where)


def check_tags(tags, name):
    if not isinstance(tags, list) or not tags:
        raise Refused("%s: no tags" % name)
    for tag in tags:
        check_tag(tag, name)
    names = [t["tag"] for t in tags]
    if len(set(names)) != len(names):
        raise Refused("%s: duplicate tags" % name)


def parse_receipt(name, data):
    """Returns the parsed receipt or raises Refused."""
    match = FILE_RE.fullmatch(name)
    if not match:
        raise Refused("%s: file name is not receipt-yyyy-MM.json" % name)
    try:
        receipt = json.loads(data.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise Refused("%s: not valid JSON" % name)
    if not isinstance(receipt, dict):
        raise Refused("%s: JSON root is not an object" % name)
    if not is_int(receipt.get("version")) or receipt["version"] != 1:
        raise Refused("%s: version is not 1" % name)
    if receipt.get("month") != match.group(1):
        raise Refused("%s: month %r does not match the file name" % (name, receipt.get("month")))
    snapshot = receipt.get("snapshot")
    if not isinstance(snapshot, str) or not SNAPSHOT_RE.fullmatch(snapshot):
        raise Refused("%s: snapshot is empty or not auction-archive-yyyymmddThhmmZ (not stamped by snapshot.sh?)" % name)
    check_time(receipt.get("createdAt"), name)
    check_tags(receipt.get("tags"), name)
    return receipt


def list_receipt_files(directory):
    if not os.path.isdir(directory):
        raise Refused("receipts directory %s does not exist" % directory)
    names = sorted(n for n in os.listdir(directory) if n.startswith("receipt-"))
    if not names:
        raise Refused("no receipt-*.json files in %s" % directory)
    return names


def select_names(names, months):
    if not months:
        return names
    for month in months:
        if not MONTH_RE.fullmatch(month):
            raise Refused("--month %r is not yyyy-MM" % month)
    wanted = {"receipt-%s.json" % m for m in months}
    missing = sorted(wanted - set(names))
    if missing:
        raise Refused("no receipt file for: " + ", ".join(missing))
    return [n for n in names if n in wanted]


def read_file(directory, name):
    try:
        with open(os.path.join(directory, name), "rb") as fh:
            return fh.read()
    except OSError as e:
        raise Refused("%s: cannot read (%s)" % (name, type(e).__name__))


def check_single_generation(receipts):
    snapshots = {r["snapshot"] for r in receipts}
    if len(snapshots) > 1:
        raise Refused("receipts carry different snapshots (%s); one generation per run" % ", ".join(sorted(snapshots)))


def load_plan(directory, months):
    """Validates EVERY receipt file in the directory; returns the Items of the selected months. Raises Refused."""
    all_names = list_receipt_files(directory)
    selected = select_names(all_names, months)
    items, receipts = {}, []
    for name in all_names:
        data = read_file(directory, name)
        receipt = parse_receipt(name, data)
        receipts.append(receipt)
        items[name] = Item(receipt["month"], META_KEY % receipt["month"], data, receipt["snapshot"])
    check_single_generation(receipts)
    return [items[n] for n in selected]


# --------------------------------------------------------------------------- snapshot hold

def run_command(argv):
    """Returns (exit code, stdout); the default runner for zfs."""
    try:
        done = subprocess.run(argv, capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError) as e:
        return 127, type(e).__name__
    return done.returncode, done.stdout


def check_snapshot_hold(runner, dataset, snapshot, tag):
    """Refuses unless the snapshot exists and carries the hold tag."""
    full = "%s@%s" % (dataset, snapshot)
    code, out = runner(["zfs", "holds", "-H", full])
    if code != 0:
        raise Refused("zfs holds %s failed (exit %s): snapshot missing or zfs unavailable" % (full, code))
    tags = {line.split("\t")[1] for line in out.splitlines() if line.count("\t") >= 1}
    if tag not in tags:
        raise Refused("snapshot %s has no hold tag %s" % (full, tag))


# --------------------------------------------------------------------------- API token credentials

def open_cloudflare_connection():
    """Seam for tests: a fresh TLS connection to the Cloudflare API host (the only host the token is ever sent to)."""
    return http.client.HTTPSConnection(CF_HOST, timeout=CF_TIMEOUT, context=ssl.create_default_context())


def cloudflare_get(url, token):
    """Default HTTP seam: returns (status, body bytes). Never follows redirects (a 3xx is refused) so the bearer
    token goes to api.cloudflare.com only. Raises Refused on network problems without details that could leak."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.hostname != CF_HOST:
        raise Refused("token verify URL is not on %s" % CF_HOST)
    headers = {"Authorization": "Bearer " + token, "Accept": "application/json"}
    try:
        conn = open_cloudflare_connection()
        try:
            conn.request("GET", parts.path, headers=headers)
            resp = conn.getresponse()
            status, body = resp.status, resp.read()
        finally:
            conn.close()
    except (http.client.HTTPException, OSError) as e:
        raise Refused("token verify request failed: %s" % type(e).__name__)
    if 300 <= status < 400:
        raise Refused("token verify: unexpected redirect (HTTP %d) refused" % status)
    return status, body


def check_file_mode(path):
    try:
        mode = os.stat(path).st_mode
    except OSError:
        raise Refused("credentials file %s not found" % path)
    if mode & (stat.S_IRWXG | stat.S_IRWXO):
        raise Refused("credentials file %s must not be accessible by group/others (chmod 600)" % path)


def content_lines(path):
    with open(path, "r", encoding="utf-8-sig") as fh:
        lines = [raw.strip() for raw in fh]
    return [line for line in lines if line and not line.startswith("#")]


def read_token_file(path):
    """Returns (token, values): the API token and the KEY=VALUE pairs of the file (empty for a bare token file)."""
    try:
        lines = content_lines(path)
    except (OSError, UnicodeDecodeError):
        raise Refused("credentials file %s cannot be read" % path)
    if len(lines) == 1 and "=" not in lines[0] and not any(c.isspace() for c in lines[0]):
        return checked_token(lines[0]), {}
    values = parse_env_file_bom_safe(path)
    token = next((values[k] for k in TOKEN_KEYS if values.get(k)), "")
    if not token:
        raise Refused("credentials file %s has neither the 4 R2 keys nor an API token (bare line, R2_API_TOKEN= or CLOUDFLARE_API_TOKEN=)" % path)
    return checked_token(token), values


def checked_token(token):
    if not TOKEN_RE.fullmatch(token):
        raise Refused("API token has an unexpected format (expected 20+ characters of A-Z a-z 0-9 _ -)")
    return token


def parse_env_file_bom_safe(path):
    """mirror.parse_env_file with a leading UTF-8 BOM dropped from the first key."""
    return {k.lstrip("\ufeff"): v for k, v in mirror.parse_env_file(path).items()}


def endpoint_settings(values, endpoint_env_file):
    """R2_ENDPOINT / R2_BUCKET / R2_REGION from the token file, falling back to the endpoint env file."""
    wanted = ("R2_ENDPOINT", "R2_BUCKET", "R2_REGION")
    found = {k: values[k] for k in wanted if values.get(k)}
    if "R2_ENDPOINT" in found and "R2_BUCKET" in found:
        return require_https(found)
    path = os.path.expanduser(endpoint_env_file)
    try:
        fallback = mirror.parse_env_file(path)
    except (OSError, UnicodeDecodeError):
        raise Refused("endpoint env file %s cannot be read (R2_ENDPOINT/R2_BUCKET needed with an API token)" % path)
    merged = {k: fallback[k] for k in wanted if fallback.get(k)} | found
    missing = [k for k in ("R2_ENDPOINT", "R2_BUCKET") if k not in merged]
    if missing:
        raise Refused("R2 settings missing: %s (token file and %s)" % (", ".join(missing), path))
    return require_https(merged)


def require_https(settings):
    if not settings["R2_ENDPOINT"].lower().startswith("https://"):
        raise Refused("R2_ENDPOINT must be an https:// URL in API token mode")
    return settings


def account_id_of(endpoint):
    host = urllib.parse.urlsplit(endpoint).hostname or ""
    match = ENDPOINT_HOST_RE.fullmatch(host.lower())
    if not match:
        raise Refused("cannot derive the Cloudflare account id from the R2 endpoint host (expected <32 hex>.r2.cloudflarestorage.com)")
    return match.group(1)


def verify_urls(token, account_id):
    account = "%s/accounts/%s/tokens/verify" % (CF_API, account_id)
    user = CF_API + "/user/tokens/verify"
    return [account] if token.startswith("cfat_") else [user, account]


def verified_token_id(body, redact):
    """Token id from a verify response, or raises Refused."""
    try:
        data = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise Refused("token verify: response is not JSON")
    result = data.get("result") if isinstance(data, dict) else None
    if not isinstance(result, dict) or data.get("success") is not True:
        errors = data.get("errors") if isinstance(data, dict) else None
        raise Refused("token verify failed: %s" % redact(json.dumps(errors))[:300])
    if result.get("status") != "active":
        raise Refused("token is not active (status %s)" % redact(result.get("status"))[:40])
    if not isinstance(result.get("id"), str) or not TOKEN_ID_RE.fullmatch(result["id"]):
        raise Refused("token verify: response has no valid token id (32 hex chars expected)")
    return result["id"]


def lookup_token_id(token, account_id, http_get, redact):
    last = None
    for url in verify_urls(token, account_id):
        status, body = http_get(url, token)
        try:
            return verified_token_id(body, redact)
        except Refused as e:
            last = Refused("%s (HTTP %s)" % (e, status))
    raise last


def token_credentials(path, endpoint_env_file, http_get):
    """S3 credentials derived from an API token file (access key id = token id, secret = sha256 hex of the token)."""
    token, values = read_token_file(path)
    redact = Redactor([token])
    try:
        settings = endpoint_settings(values, endpoint_env_file)
        token_id = lookup_token_id(token, account_id_of(settings["R2_ENDPOINT"]), http_get, redact)
    except Refused as e:
        raise Refused(redact(e))
    secret = hashlib.sha256(token.encode("utf-8")).hexdigest()
    return {"R2_ENDPOINT": settings["R2_ENDPOINT"], "R2_BUCKET": settings["R2_BUCKET"],
            "R2_REGION": settings.get("R2_REGION") or "auto", "R2_ACCESS_KEY_ID": token_id,
            "R2_SECRET_ACCESS_KEY": secret, "R2_API_TOKEN": token}


def load_write_credentials(env_file, endpoint_env_file, http_get):
    """The full 4-key env file behaves as before; anything else is read as an API token file."""
    path = os.path.expanduser(env_file)
    check_file_mode(path)
    try:
        values = mirror.parse_env_file(path)
    except (OSError, UnicodeDecodeError):
        raise Refused("credentials file %s cannot be read" % path)
    if all(values.get(k) for k in mirror.ENV_KEYS):
        return mirror.load_credentials(path)
    return token_credentials(path, endpoint_env_file, http_get)


# --------------------------------------------------------------------------- R2 access

class R2Store(S3Client):
    """S3Client plus whole-object GET and PUT with a body."""

    def _send(self, method, key, body=b"", extra=None):
        path = "%s/%s/%s" % (self.base_path, self.bucket, key)
        payload_hash = hashlib.sha256(body).hexdigest()
        amz_date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        headers = {"host": self.host_header, "x-amz-content-sha256": payload_hash, "x-amz-date": amz_date}
        headers.update(extra or {})
        auth = mirror.sign_request(method, path, {}, headers, payload_hash, self.access_key, self.secret, amz_date, self.region)
        send = dict(headers)
        send["Authorization"] = auth
        try:
            conn = self._conn(fresh=True)
            conn.request(method, mirror.uri_encode(path, encode_slash=False), body=body or None, headers=send)
            resp = conn.getresponse()
            return resp.status, resp.read()
        except (http.client.HTTPException, OSError) as e:
            raise MirrorError("network error: %s" % self.redact(type(e).__name__ + ": " + str(e)))

    def _expect(self, status, body, what, ok=(200,)):
        if status in ok:
            return
        text = self.redact(re.sub(r"\s+", " ", body[:2048].decode("utf-8", "replace")))[:300]
        raise MirrorError("%s: HTTP %d %s" % (what, status, text))

    def get_bytes(self, key):
        """Object bytes or None when it does not exist."""
        status, body = self._send("GET", key)
        if status == 404 and b"<Code>NoSuchKey</Code>" in body:
            return None
        self._expect(status, body, "GetObject")
        return body

    def put_bytes(self, key, data):
        md5 = base64.b64encode(hashlib.md5(data).digest()).decode("ascii")
        extra = {"content-type": "application/json", "content-md5": md5}
        status, body = self._send("PUT", key, data, extra)
        self._expect(status, body, "PutObject")


# --------------------------------------------------------------------------- upload

Inspection = collections.namedtuple("Inspection", "item status remote_snapshot error")


def classify(existing, data):
    if existing is None:
        return "new"
    return "unchanged" if existing == data else "replaced"


def remote_snapshot_of(existing):
    """Snapshot name of the receipt currently in R2, None when absent or unreadable."""
    if existing is None:
        return None
    try:
        value = json.loads(existing.decode("utf-8")).get("snapshot")
    except (ValueError, UnicodeDecodeError, AttributeError):
        return None
    return value if isinstance(value, str) and SNAPSHOT_RE.fullmatch(value) else None


def inspect_item(store, item, redact):
    try:
        existing = store.get_bytes(item.key)
    except (MirrorError, OSError) as e:
        return Inspection(item, "", None, redact(e))
    return Inspection(item, classify(existing, item.data), remote_snapshot_of(existing), None)


def stamp(snapshot):
    return SNAPSHOT_RE.fullmatch(snapshot).group(1)


def refuse_older_overwrite(inspections, allow_older):
    """The remote receipt must not name a newer snapshot than the one being uploaded."""
    if allow_older:
        return
    for i in inspections:
        if i.remote_snapshot and stamp(i.remote_snapshot) > stamp(i.item.snapshot):
            raise Refused("%s: R2 has the NEWER snapshot %s, local is %s (use --allow-older to overwrite)"
                          % (i.item.month, i.remote_snapshot, i.item.snapshot))


def describe(i, execute):
    status = i.status
    if status == "replaced":
        status += " (%s -> %s)" % (i.remote_snapshot or "unknown", i.item.snapshot)
    return status if execute else "would be " + status


def apply_item(store, i):
    """Uploads one inspected month and reads it back; raises MirrorError on any problem."""
    store.put_bytes(i.item.key, i.item.data)
    if store.get_bytes(i.item.key) != i.item.data:
        raise MirrorError("READ-BACK MISMATCH")


def finish_item(store, i, execute, redact):
    """Returns (text, ok)."""
    if i.error:
        return "FAILED: " + i.error, False
    if not execute or i.status == "unchanged":
        return describe(i, execute), True
    try:
        apply_item(store, i)
    except (MirrorError, OSError) as e:
        return "FAILED: " + redact(e), False
    return describe(i, execute), True


def run_plan(store, plan, execute, allow_older, out, redact):
    inspections = [inspect_item(store, item, redact) for item in plan]
    refuse_older_overwrite(inspections, allow_older)
    failures = 0
    for i in inspections:
        text, ok = finish_item(store, i, execute, redact)
        failures += 0 if ok else 1
        print("%s  %s  %s" % (i.item.month, i.item.key, text), file=out)
    return failures


def make_store(creds):
    redact = Redactor([creds["R2_ACCESS_KEY_ID"], creds["R2_SECRET_ACCESS_KEY"], creds.get("R2_API_TOKEN")])
    store = R2Store(creds["R2_ENDPOINT"], creds["R2_BUCKET"], creds["R2_ACCESS_KEY_ID"],
                    creds["R2_SECRET_ACCESS_KEY"], creds["R2_REGION"], redact=redact)
    return store, redact


def parse_args(argv):
    p = argparse.ArgumentParser(description="Upload stamped backup receipts to R2")
    p.add_argument("--receipts", required=True, help="the with-snapshot directory written by snapshot.sh")
    p.add_argument("--env-file", default=DEFAULT_ENV_FILE)
    p.add_argument("--endpoint-env-file", default=DEFAULT_ENDPOINT_ENV_FILE,
                   help="env file with R2_ENDPOINT/R2_BUCKET, used when --env-file holds an API token")
    p.add_argument("--month", action="append", default=[], help="yyyy-MM, repeatable; default all")
    p.add_argument("--execute", action="store_true", help="really upload (default: dry run)")
    p.add_argument("--allow-older", action="store_true", help="overwrite a receipt in R2 that names a newer snapshot")
    p.add_argument("--dataset", default=DEFAULT_DATASET, help="ZFS dataset holding the snapshot")
    p.add_argument("--hold-tag", default=DEFAULT_HOLD_TAG, help="zfs hold tag the snapshot must carry")
    return p.parse_args(argv)


def prepare(args, store_factory, runner, http_get):
    """Everything that can be refused before the first request; returns (plan, store, redact)."""
    plan = load_plan(args.receipts, args.month)
    creds = load_write_credentials(args.env_file, args.endpoint_env_file, http_get)
    check_snapshot_hold(runner, args.dataset, plan[0].snapshot, args.hold_tag)
    store, redact = store_factory(creds)
    return plan, store, redact


def main(argv=None, store_factory=make_store, out=None, runner=run_command, http_get=cloudflare_get):
    out = out or sys.stdout
    args = parse_args(argv)
    try:
        plan, store, redact = prepare(args, store_factory, runner, http_get)
    except (Refused, MirrorError) as e:
        print("refused: %s" % e, file=out)
        return 2
    except Exception as e:  # credential loading must never print a traceback (it could carry the token)
        print("refused: unexpected %s while preparing" % type(e).__name__, file=out)
        return 2
    print("%d receipt(s), snapshot %s, %s" % (len(plan), plan[0].snapshot,
                                              "EXECUTE" if args.execute else "dry run (no writes)"), file=out)
    try:
        failures = run_plan(store, plan, args.execute, args.allow_older, out, redact)
    except Refused as e:
        print("refused: %s" % e, file=out)
        return 2
    except Exception as e:  # never let a traceback (which could carry request details) out
        print("failed: %s" % redact(type(e).__name__ + ": " + str(e)), file=out)
        return 1
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
