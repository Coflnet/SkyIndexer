#!/usr/bin/env python3
"""Mirror the Cloudflare R2 auction archive (prefix ``auctions/``) to a local/NAS directory.

Stdlib only (own AWS SigV4 for ListObjectsV2 / GetObject / HeadObject).

    python3 -I mirror.py --dest /storage/backup/hypixel/sky-auctions-archive [--workers 16] [--dry-run]

Credentials come from an env file (default ~/.config/coflnet/sky-auctions-r2-ro.env, mode 600):
    R2_ENDPOINT=https://<account>.r2.cloudflarestorage.com
    R2_ACCESS_KEY_ID=...
    R2_SECRET_ACCESS_KEY=...
    R2_BUCKET=sky-auctions-archive
    R2_REGION=auto            (optional)

Guarantees: temp file + fsync + atomic rename; MD5 compared to the ETag (single-part objects);
resumable (already verified objects are skipped); local files are NEVER deleted (objects that
vanished from R2 are logged as ``gone`` in the manifest); secrets are never printed or logged.
Exit code 0 = everything ok, 1 = at least one object failed, 2 = refused / bad configuration.
"""
import argparse
import concurrent.futures
import datetime
import glob
import hashlib
import hmac
import http.client
import os
import random
import re
import socket
import ssl
import stat
import subprocess
import sys
import threading
import time
import urllib.parse
import xml.etree.ElementTree as ET

TMP_SUFFIX = ".mirror-tmp"
MANIFEST_GLOB = "_manifest-*.tsv"
RUN_LOG = "_backup-run.log"
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
CHUNK = 1024 * 1024

DEFAULT_ENV_FILE = "~/.config/coflnet/sky-auctions-r2-ro.env"
ENV_KEYS = ("R2_ENDPOINT", "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY", "R2_BUCKET")


class MirrorError(Exception):
    """Raised for failures whose message is safe to print."""


class RetryableError(MirrorError):
    pass


# --------------------------------------------------------------------------- SigV4

def _hmac(key, msg):
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


def signing_key(secret, datestamp, region, service):
    k = _hmac(("AWS4" + secret).encode("utf-8"), datestamp)
    k = _hmac(k, region)
    k = _hmac(k, service)
    return _hmac(k, "aws4_request")


def uri_encode(value, encode_slash=True):
    safe = "-_.~" + ("" if encode_slash else "/")
    return urllib.parse.quote(value, safe=safe)


def canonical_query(params):
    pairs = sorted((uri_encode(str(k)), uri_encode(str(v))) for k, v in params.items())
    return "&".join("%s=%s" % p for p in pairs)


def canonical_request(method, path, params, headers, payload_hash):
    """headers: dict name->value, all of them are signed. Path is the raw (unencoded) path."""
    norm = {k.lower(): " ".join(str(v).strip().split()) for k, v in headers.items()}
    names = sorted(norm)
    canon_headers = "".join("%s:%s\n" % (n, norm[n]) for n in names)
    signed = ";".join(names)
    req = "\n".join([method, uri_encode(path, encode_slash=False), canonical_query(params),
                     canon_headers, signed, payload_hash])
    return req, signed


def sign_request(method, path, params, headers, payload_hash, access_key, secret, amz_date, region, service="s3"):
    """Returns the Authorization header value. ``headers`` must already contain host and x-amz-* headers."""
    datestamp = amz_date[:8]
    req, signed = canonical_request(method, path, params, headers, payload_hash)
    scope = "%s/%s/%s/aws4_request" % (datestamp, region, service)
    to_sign = "\n".join(["AWS4-HMAC-SHA256", amz_date, scope, hashlib.sha256(req.encode("utf-8")).hexdigest()])
    sig = hmac.new(signing_key(secret, datestamp, region, service), to_sign.encode("utf-8"), hashlib.sha256).hexdigest()
    return "AWS4-HMAC-SHA256 Credential=%s/%s, SignedHeaders=%s, Signature=%s" % (access_key, scope, signed, sig)


# --------------------------------------------------------------------------- credentials / redaction

def parse_env_file(path):
    values = {}
    with open(path, "r", encoding="utf-8") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("export "):
                line = line[7:].lstrip()
            if "=" not in line:
                continue
            k, v = line.split("=", 1)
            v = v.strip()
            if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
                v = v[1:-1]
            values[k.strip()] = v
    return values


def load_credentials(path):
    path = os.path.expanduser(path)
    try:
        mode = os.stat(path).st_mode
    except OSError:
        raise MirrorError("credentials file %s not found" % path)
    if mode & (stat.S_IRWXG | stat.S_IRWXO):
        raise MirrorError("credentials file %s must not be accessible by group/others (chmod 600)" % path)
    values = parse_env_file(path)
    missing = [k for k in ENV_KEYS if not values.get(k)]
    if missing:
        raise MirrorError("credentials file is missing keys: " + ", ".join(missing))
    return {k: values[k] for k in ENV_KEYS} | {"R2_REGION": values.get("R2_REGION") or "auto"}


class Redactor:
    def __init__(self, secrets=()):
        self.secrets = [s for s in secrets if s and len(s) >= 4]

    def __call__(self, text):
        text = str(text)
        for s in self.secrets:
            text = text.replace(s, "***")
        return text


# --------------------------------------------------------------------------- S3 client

class S3Client:
    def __init__(self, endpoint, bucket, access_key, secret, region="auto", timeout=60, redact=None):
        u = urllib.parse.urlsplit(endpoint)
        if u.scheme not in ("https", "http") or not u.hostname:
            raise MirrorError("R2_ENDPOINT must be an http(s) URL")
        self.scheme, self.host, self.port = u.scheme, u.hostname, u.port
        self.host_header = u.netloc.split("@")[-1]
        self.base_path = u.path.rstrip("/")
        self.bucket, self.access_key, self.secret = bucket, access_key, secret
        self.region, self.timeout = region, timeout
        self.redact = redact or Redactor([access_key, secret])
        self._local = threading.local()

    def _conn(self, fresh=False):
        c = getattr(self._local, "conn", None)
        if c is not None and not fresh:
            return c
        if c is not None:
            try:
                c.close()
            except Exception:
                pass
        if self.scheme == "https":
            c = http.client.HTTPSConnection(self.host, self.port, timeout=self.timeout, context=ssl.create_default_context())
        else:
            c = http.client.HTTPConnection(self.host, self.port, timeout=self.timeout)
        self._local.conn = c
        return c

    def _request(self, method, key_or_none, params=None):
        params = params or {}
        path = "%s/%s" % (self.base_path, self.bucket)
        if key_or_none is not None:
            path += "/" + key_or_none
        amz_date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        headers = {"host": self.host_header, "x-amz-content-sha256": EMPTY_SHA256, "x-amz-date": amz_date}
        auth = sign_request(method, path, params, headers, EMPTY_SHA256, self.access_key, self.secret, amz_date, self.region)
        send = dict(headers)
        send["Authorization"] = auth
        target = uri_encode(path, encode_slash=False)
        if params:
            target += "?" + canonical_query(params)
        try:
            conn = self._conn()
            try:
                conn.request(method, target, headers=send)
                return conn.getresponse()
            except (http.client.HTTPException, ConnectionError, socket.timeout, ssl.SSLError, OSError):
                conn = self._conn(fresh=True)  # stale keep-alive connection: one immediate retry
                conn.request(method, target, headers=send)
                return conn.getresponse()
        except (http.client.HTTPException, OSError) as e:
            self._conn(fresh=True)
            raise RetryableError("network error: %s" % self.redact(type(e).__name__ + ": " + str(e)))

    def _check(self, resp, what):
        if resp.status == 200:
            return
        body = resp.read(2048).decode("utf-8", "replace")
        resp.close()
        self._conn(fresh=True)
        msg = "%s: HTTP %d %s" % (what, resp.status, self.redact(re.sub(r"\s+", " ", body))[:300])
        if resp.status >= 500 or resp.status in (408, 429):
            raise RetryableError(msg)
        raise MirrorError(msg)

    def list(self, prefix):
        """Yields (key, size, etag) for every object under prefix."""
        token = None
        while True:
            params = {"list-type": "2", "prefix": prefix, "encoding-type": "url", "max-keys": "1000"}
            if token:
                params["continuation-token"] = token
            root = None
            for attempt in range(6):
                try:
                    resp = self._request("GET", None, params)
                    self._check(resp, "ListObjectsV2")
                    root = ET.fromstring(resp.read())
                    break
                except (RetryableError, ET.ParseError) as e:
                    if attempt == 5:
                        raise MirrorError("listing failed: %s" % e)
                    time.sleep(min(30, 2 ** attempt) + random.random())
            ns = root.tag[:root.tag.index("}") + 1] if root.tag.startswith("{") else ""
            for c in root.findall(ns + "Contents"):
                key = urllib.parse.unquote(c.findtext(ns + "Key"))
                yield key, int(c.findtext(ns + "Size")), (c.findtext(ns + "ETag") or "").strip('"')
            if (root.findtext(ns + "IsTruncated") or "").lower() == "true":
                token = root.findtext(ns + "NextContinuationToken")
                if not token:
                    raise MirrorError("listing truncated without continuation token")
            else:
                return

    def head(self, key):
        resp = self._request("HEAD", key)
        try:
            self._check(resp, "HeadObject")
            return int(resp.getheader("Content-Length")), (resp.getheader("ETag") or "").strip('"')
        finally:
            resp.read()

    def get(self, key, fileobj):
        """Streams the object into fileobj; returns (bytes, md5hex, etag header)."""
        resp = self._request("GET", key)
        self._check(resp, "GetObject")
        md5, n = hashlib.md5(), 0
        expected = resp.getheader("Content-Length")
        try:
            while True:
                chunk = resp.read(CHUNK)
                if not chunk:
                    break
                md5.update(chunk)
                fileobj.write(chunk)
                n += len(chunk)
        except (http.client.HTTPException, OSError) as e:
            self._conn(fresh=True)
            raise RetryableError("download interrupted: %s" % self.redact(type(e).__name__ + ": " + str(e)))
        if expected is not None and int(expected) != n:
            self._conn(fresh=True)
            raise RetryableError("short read %d of %s bytes" % (n, expected))
        return n, md5.hexdigest(), (resp.getheader("ETag") or "").strip('"')


# --------------------------------------------------------------------------- local state

def safe_local_path(dest, key):
    if not key.startswith("auctions/") or "\x00" in key or "\\" in key:
        raise MirrorError("refusing unsafe key %r" % key)
    parts = key.split("/")
    if any(p in ("", ".", "..") for p in parts):
        raise MirrorError("refusing unsafe key %r" % key)
    return os.path.join(dest, *parts)


def md5_file(path):
    h = hashlib.md5()
    with open(path, "rb") as fh:
        while True:
            b = fh.read(CHUNK)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def is_multipart(etag):
    return "-" in etag


def load_manifests(dest):
    """Merged key -> dict(size, etag, md5, status) of all manifests, oldest first (newer wins).
    Understands the old 4 column format (key,size,etag,status) and the new 5 column one."""
    entries = {}
    for path in sorted(glob.glob(os.path.join(dest, MANIFEST_GLOB))):
        try:
            with open(path, "r", encoding="utf-8") as fh:
                for line in fh:
                    cols = line.rstrip("\n").split("\t")
                    if len(cols) < 4 or cols[0] == "key":
                        continue
                    if len(cols) >= 5:
                        key, size, etag, md5, status = cols[:5]
                    else:
                        key, size, etag, status = cols[:4]
                        md5 = ""
                    try:
                        entries[key] = {"size": int(size), "etag": etag, "md5": md5, "status": status}
                    except ValueError:
                        continue
        except OSError:
            continue
    return entries


def clean_stale_tmp(dest):
    n = 0
    for root, _dirs, files in os.walk(os.path.join(dest, "auctions")):
        for f in files:
            if f.endswith(TMP_SUFFIX):
                try:
                    os.unlink(os.path.join(root, f))
                    n += 1
                except OSError:
                    pass
    return n


def local_keys(dest):
    out = []
    base = os.path.join(dest, "auctions")
    for root, _dirs, files in os.walk(base):
        for f in files:
            if f.endswith(TMP_SUFFIX):
                continue
            rel = os.path.relpath(os.path.join(root, f), dest)
            out.append(rel.replace(os.sep, "/"))
    return out


def fsync_dir(path):
    fd = os.open(path, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


class Result:
    __slots__ = ("key", "size", "etag", "md5", "status")

    def __init__(self, key, size, etag, md5, status):
        self.key, self.size, self.etag, self.md5, self.status = key, size, etag, md5, status

    def row(self):
        return "\t".join([self.key, str(self.size), self.etag, self.md5, self.status])

    @property
    def failed(self):
        return self.status.startswith("failed")


def decide_skip(path, size, etag, prev):
    """Returns (skip, md5). skip=True when the local copy provably equals the R2 object."""
    try:
        st = os.stat(path)
    except OSError:
        return False, ""
    if not stat.S_ISREG(st.st_mode) or st.st_size != size:
        return False, ""
    if is_multipart(etag):
        # md5 can not be compared with a multipart ETag; trust only a previous verified manifest entry
        if prev and prev["etag"] == etag and prev["size"] == size and prev["status"] in ("ok-multipart", "skipped-multipart"):
            return True, prev.get("md5", "")
        return False, ""
    md5 = md5_file(path)
    return (md5 == etag), md5


def process_object(client, dest, key, size, etag, prev, sleep=time.sleep, attempts=6):
    path = safe_local_path(dest, key)
    skip, md5 = decide_skip(path, size, etag, prev)
    if skip:
        return Result(key, size, etag, md5, "skipped-multipart" if is_multipart(etag) else "skipped")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = "%s.%d.%d%s" % (path, os.getpid(), threading.get_ident(), TMP_SUFFIX)
    last = "unknown"
    for attempt in range(attempts):
        try:
            with open(tmp, "wb") as fh:
                n, got_md5, header_etag = client.get(key, fh)
                fh.flush()
                os.fsync(fh.fileno())
            if n != size:
                raise RetryableError("size %d != listed %d" % (n, size))
            if not is_multipart(etag) and got_md5 != etag:
                raise RetryableError("md5 %s != etag %s" % (got_md5, etag))
            os.replace(tmp, path)
            fsync_dir(os.path.dirname(path))
            return Result(key, size, etag, got_md5, "ok-multipart" if is_multipart(etag) else "ok")
        except RetryableError as e:
            last = str(e)
        except MirrorError as e:
            last = str(e)
            break
        except OSError as e:
            last = "%s: %s" % (type(e).__name__, e)
            break
        finally:
            if os.path.exists(tmp):
                try:
                    os.unlink(tmp)
                except OSError:
                    pass
        if attempt < attempts - 1:
            sleep(min(60, 2 ** attempt) + random.random())
    return Result(key, size, etag, "", "failed:" + re.sub(r"[\t\r\n]+", " ", last)[:200])


def write_manifest(dest, results, ts):
    final = os.path.join(dest, "_manifest-%s.tsv" % ts)
    tmp = final + ".partial"
    with open(tmp, "w", encoding="utf-8") as fh:
        fh.write("key\tsize\tetag\tmd5\tstatus\n")
        for r in sorted(results, key=lambda r: r.key):
            fh.write(r.row() + "\n")
        fh.flush()
        os.fsync(fh.fileno())
    os.replace(tmp, final)
    fsync_dir(dest)
    return final


def check_zfs(dataset, run=subprocess.run):
    try:
        p = run(["zfs", "get", "-H", "-o", "property,value", "keystatus,mounted", dataset],
                capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError) as e:
        raise MirrorError("cannot run zfs to check %s: %s (use --skip-zfs-check to override)" % (dataset, type(e).__name__))
    if p.returncode != 0:
        raise MirrorError("zfs get %s failed: %s" % (dataset, p.stderr.strip()[:200]))
    props = dict(line.split("\t", 1) for line in p.stdout.strip().splitlines() if "\t" in line)
    if props.get("mounted") != "yes":
        raise MirrorError("dataset %s is not mounted" % dataset)
    if props.get("keystatus") not in ("available", "-"):
        raise MirrorError("dataset %s key is not loaded (keystatus=%s)" % (dataset, props.get("keystatus")))


def run_mirror(client, dest, workers=16, dry_run=False, prefix="auctions/", log=print, sleep=time.sleep):
    """Returns (results, manifest_path_or_None)."""
    if not os.path.isdir(dest):
        raise MirrorError("destination %s does not exist (refusing to create it: is the dataset mounted?)" % dest)
    removed = clean_stale_tmp(dest)
    if removed:
        log("removed %d stale temp files" % removed)
    prev = load_manifests(dest)
    log("previous manifests: %d entries" % len(prev))
    listing = {}
    for key, size, etag in client.list(prefix):
        listing[key] = (size, etag)
    log("R2 listing: %d objects, %.2f GiB" % (len(listing), sum(s for s, _ in listing.values()) / 2 ** 30))

    results, todo = [], []
    for key, (size, etag) in listing.items():
        if key.endswith("/"):
            results.append(Result(key, size, etag, "", "skipped-dir"))
        else:
            todo.append(key)
    # objects that vanished from R2: never delete, only record
    if prefix == "auctions/":
        for key in local_keys(dest):
            if key not in listing:
                try:
                    sz = os.path.getsize(safe_local_path(dest, key))
                except (OSError, MirrorError):
                    continue
                old = prev.get(key, {})
                results.append(Result(key, sz, old.get("etag", ""), old.get("md5", ""), "gone"))

    if dry_run:
        need = 0
        for key in todo:
            size, etag = listing[key]
            skip, _ = decide_skip(safe_local_path(dest, key), size, etag, prev.get(key))
            need += 0 if skip else 1
        gone = sum(1 for r in results if r.status == "gone")
        log("dry run: %d of %d objects need download, %d gone from R2" % (need, len(todo), gone))
        return results, None

    done, lock, started = [0], threading.Lock(), time.time()

    def work(key):
        size, etag = listing[key]
        try:
            r = process_object(client, dest, key, size, etag, prev.get(key), sleep=sleep)
        except MirrorError as e:
            r = Result(key, size, etag, "", "failed:" + str(e)[:200])
        with lock:
            done[0] += 1
            if done[0] % 500 == 0:
                log("progress %d/%d (%.0fs)" % (done[0], len(todo), time.time() - started))
        return r

    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as ex:
        results.extend(ex.map(work, todo))
    ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = write_manifest(dest, results, ts)
    return results, path


def summarize(results):
    counts = {}
    for r in results:
        k = r.status.split(":")[0]
        counts[k] = counts.get(k, 0) + 1
    return counts


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dest", default="/storage/backup/hypixel/sky-auctions-archive")
    ap.add_argument("--env-file", default=DEFAULT_ENV_FILE)
    ap.add_argument("--workers", type=int, default=16)
    ap.add_argument("--dry-run", action="store_true", help="list and plan only, download nothing")
    ap.add_argument("--zfs-dataset", default="storage/backup", help="dataset that must be mounted with its key loaded")
    ap.add_argument("--skip-zfs-check", action="store_true")
    args = ap.parse_args(argv)

    redact = Redactor()
    logfile = None

    def log(msg):
        line = "%s %s" % (datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), redact(msg))
        print(line, flush=True)
        if logfile:
            logfile.write(line + "\n")
            logfile.flush()

    try:
        creds = load_credentials(args.env_file)
        redact.secrets = [creds["R2_ACCESS_KEY_ID"], creds["R2_SECRET_ACCESS_KEY"]]
        if not args.skip_zfs_check:
            check_zfs(args.zfs_dataset)
        if not os.path.isdir(args.dest):
            raise MirrorError("destination %s does not exist (refusing to create it: is the dataset mounted?)" % args.dest)
        if not args.dry_run:
            logfile = open(os.path.join(args.dest, RUN_LOG), "a", encoding="utf-8")
        client = S3Client(creds["R2_ENDPOINT"], creds["R2_BUCKET"], creds["R2_ACCESS_KEY_ID"],
                          creds["R2_SECRET_ACCESS_KEY"], creds["R2_REGION"], redact=redact)
        log("start dest=%s workers=%d dry_run=%s" % (args.dest, args.workers, args.dry_run))
        results, manifest = run_mirror(client, args.dest, args.workers, args.dry_run, log=log)
    except MirrorError as e:
        print("error: " + redact(e), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("interrupted; rerun to resume", file=sys.stderr)
        return 130
    except Exception as e:  # no traceback: it could contain request details
        print("unexpected %s: %s" % (type(e).__name__, redact(e)), file=sys.stderr)
        return 2
    counts = summarize(results)
    log("summary %s manifest=%s" % (counts, manifest))
    failed = [r for r in results if r.failed]
    for r in failed[:20]:
        log("FAILED %s %s" % (r.key, r.status))
    if logfile:
        logfile.close()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
