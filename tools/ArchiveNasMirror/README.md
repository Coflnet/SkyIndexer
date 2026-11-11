# ArchiveNasMirror

Mirrors the Cloudflare R2 auction archive (bucket `sky-auctions-archive`, prefix `auctions/`) to the NAS, proves the
copy is byte- and content-identical, pins it with a held ZFS snapshot and produces one backup receipt per month.
The receipts are the input for the gates that allow deleting a month from Scylla/MariaDB.

| Part | Purpose |
| --- | --- |
| `mirror.py` | stdlib-only R2 to disk mirror (own SigV4), resumable, MD5 vs ETag, never deletes |
| `ArchiveNasVerify/` | .NET console verifier: decodes every block, recomputes `Sha256()`, writes `verify-*.json` + `receipt-*.json` |
| `snapshot.sh` | health checks, `zfs snapshot` + `zfs hold`, re-read through the snapshot, stamps the receipts |
| `test_mirror.py`, `test_snapshot.py`, `ArchiveNasVerify.Tests/` | tests (no network, no R2, no MariaDB) |

## One-time setup

1. R2 read-only token (Object Read only, scoped to bucket `sky-auctions-archive`). On the NAS create
   `~/.config/coflnet/sky-auctions-r2-ro.env` with `chmod 600` (the tool refuses group/world readable files):

   ```
   R2_ENDPOINT=https://<account-id>.r2.cloudflarestorage.com
   R2_ACCESS_KEY_ID=<access key id>
   R2_SECRET_ACCESS_KEY=<secret access key>
   R2_BUCKET=sky-auctions-archive
   R2_REGION=auto            # optional, default auto
   ```

   Secret values are never printed, logged or put into error messages (they are replaced by `***`).
2. ZFS delegation, once, as root on the NAS:

   ```
   sudo zfs allow ekwav snapshot,hold,release,mount storage/backup
   ```

   `snapshot.sh` snapshots with `-r`, so it also covers `storage/backup/hypixel` if that is (or later becomes) its own
   dataset; it works out which dataset contains the mirror and reads the snapshot through that dataset's
   `.zfs/snapshot` directory. `mirror.py` and `snapshot.sh` refuse to run unless the pool is healthy
   (`snapshot.sh`), the dataset is mounted and its key is loaded (`keystatus=available`).
3. Publish the verifier (self-contained, no dotnet needed on the NAS; about 130 MB) and copy everything over:

   ```
   cd /run/media/ekwav/Data/dev/hypixel/SkyIndexer/tools/ArchiveNasMirror
   dotnet publish ArchiveNasVerify -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /tmp/archive-nas-verify-pub
   ssh ekwav@192.168.2.44 'mkdir -p ~/archive-mirror'
   scp mirror.py snapshot.sh /tmp/archive-nas-verify-pub/ArchiveNasVerify ekwav@192.168.2.44:~/archive-mirror/
   rm -rf /tmp/archive-nas-verify-pub
   ```

   (If the single file complains about a missing native library, `scp -r` the whole publish directory instead.)
   Running the verifier on the NAS matters: decoding reads the whole archive and an sshfs mount is limited to 100 Mbit.
4. Tests on the workstation (all offline):

   ```
   python3 -I -m unittest                      # mirror.py (incl. AWS SigV4 test vectors) and snapshot.sh with stubbed zfs
   dotnet test ArchiveNasVerify.Tests          # verifier
   ```

## Run order (on the NAS, wake it first: `wakeonlan 34:17:eb:9c:8e:10`; it suspends around 20:00 and 04:00)

```
cd ~/archive-mirror
python3 -I mirror.py --dry-run                           # lists R2, prints how many objects would be downloaded
python3 -I mirror.py                                     # 1. mirror (resumable: just run it again after a suspend)
./ArchiveNasVerify --root /storage/backup/hypixel/sky-auctions-archive \
    --out /storage/backup/hypixel/archive-receipts/$(date -u +%Y%m%dT%H%M%SZ)   # 2. verify, writes receipts
./snapshot.sh --receipts <that out dir>                  # 3. dry run: checks + plan
./snapshot.sh --receipts <that out dir> --execute        # 3. snapshot + hold + re-read + stamp
python3 -I upload_receipts.py --receipts <that out dir>/with-snapshot            # 4. dry run: validates, prints new/replaced/unchanged
python3 -I upload_receipts.py --receipts <that out dir>/with-snapshot --execute  # 4. PUT to R2 + read back
```

The stamped receipts end up in `<out dir>/with-snapshot/receipt-yyyy-MM.json`.

### 4. upload_receipts.py

Uploads the stamped receipts to `auctions/v2/_meta/backup/yyyy-MM.json` in R2, where SkyIndexer's retention worker looks
for them (an unstamped receipt counts as none). Needs a separate **read/write** token (Object Read & Write, scoped to
bucket `sky-auctions-archive`) in `~/.config/coflnet/sky-auctions-r2-rw.env` (`chmod 600`, same keys as the ro file;
override with `--env-file`). Instead of the 4 keys the file may hold just a Cloudflare API token (a bare single line,
or `R2_API_TOKEN=` / `CLOUDFLARE_API_TOKEN=`; `chmod 600`): endpoint, bucket and region then come from the token file or
`--endpoint-env-file` (default the ro env file), the token id (from Cloudflare's token verify API) is the access key id
and sha256(token) the secret; the token is never printed. Token mode calls api.cloudflare.com (no redirects followed) even in the dry run; an account
API token needs the permission "Workers R2 Storage Bucket Item Write" scoped to `sky-auctions-archive`. Everything is validated first (name `receipt-yyyy-MM.json`, version 1, month equals file name,
snapshot `auction-archive-yyyymmddThhmmZ`, tags/objects with key/etag/size, one snapshot generation in the directory);
one bad file refuses the whole run (exit 2). The dry run only reads (GET) to tell `new` / `replaced` / `unchanged`.
`--execute` PUTs (Content-Type application/json, Content-MD5), reads back and compares bytes; identical objects are not
re-uploaded. Every receipt file in the directory is validated even with `--month`. A month whose R2 receipt names a
NEWER snapshot is refused unless `--allow-older`. The snapshot must still exist with its hold (`zfs holds -H
storage/backup@<snap>` must list tag `auction-archive`; `--dataset`, `--hold-tag`), also in the dry run. `--month yyyy-MM` (repeatable) limits the months. Exit 0 ok, 1 a month failed, 2 refused/misconfigured.
Copy it with the other scripts: `scp upload_receipts.py mirror.py ekwav@192.168.2.44:~/archive-mirror/`.

### 1. mirror.py

* Lists `auctions/` (everything, v1 and v2), downloads new/changed objects with 16 parallel workers (`--workers`) to
  `<file>.<pid>.<tid>.mirror-tmp`, fsyncs, renames atomically and fsyncs the directory. Retries with exponential backoff.
* Skips an object when the local size equals the R2 size and the local MD5 equals the ETag. That reuses the files of the
  one-off mirror of 2026-10-25 (its `_manifest-*.tsv` has only `key,size,etag,status`; the md5 is recomputed locally,
  so old manifests are never trusted blindly). Multipart ETags (contain `-`, not expected for this archive) can not be
  compared with an MD5; they are size-checked and flagged `ok-multipart`/`skipped-multipart`.
* Local files are never deleted. Local files whose object vanished from R2 stay and are written as `gone` into the
  manifest (so a privacy erasure that removed a part is visible but not propagated; see rotation below).
* Writes `_manifest-<UTC ts>.tsv` (`key size etag md5 status`; status is `ok`, `skipped`, `ok-multipart`,
  `skipped-multipart`, `skipped-dir`, `gone` or `failed:<reason>`) and appends to `_backup-run.log`.
* Exit code 0 = all ok, 1 = at least one object failed (rerun resumes), 2 = refused/misconfigured.

### 2. ArchiveNasVerify

For every tag index and every month entry: reads each part file, compares MD5 with the manifest ETag (byte level),
decodes it with the real `ArchiveBlobCodec`, recomputes `ArchivedAuctionBlock.Sha256()` and compares it with
`PartSha256`, checks the part count (`Parts`, number of hashes, missing parts), `Rows` (sum of decoded rows) and
computes `CombinedSha256`. Extra part files beyond `Parts` and a `SoldRows` difference are warnings only.

Options: `--root DIR` (required), `--out DIR`, `--month yyyy-MM[,..]`, `--tag TAG[,..]`, `--parallel N`,
`--mariadb-env FILE`. With `--mariadb-env` (file containing `DBConnection=<read-only connection string>`, or the bare
connection string; the session is set `READ ONLY` and the string is never printed) the combined hash, rows and parts of
every tag-month are compared with `archived_months`, and tag-months that exist in MariaDB but not in the mirror fail
the month. The NAS probably can not reach MariaDB directly; if you want that comparison there, forward the port from the
workstation (`ssh -R 13306:<mariadb host>:3306 ekwav@192.168.2.44`, connection string `server=127.0.0.1;port=13306;...`
in an env file with mode 600 on the NAS) or leave it out and compare `combinedSha256` from `verify-*.json` yourself.

Output per month in `--out`: `verify-yyyy-MM.json` (always) and `receipt-yyyy-MM.json` (only if the month verified
completely; a stale receipt of an earlier run is deleted when the month now fails), plus `summary.json`.
Exit code 0/1/2 = verified/failed/usage.

Receipt schema (version 1, consumed by another component, do not change):

```json
{ "version": 1, "month": "2021-03", "snapshot": "", "createdAt": "2026-10-31T14:00:00.0000000Z",
  "tags": [ { "tag": "HYPERION", "parts": 2, "rows": 5, "partSha256": ["..", ".."],
              "objects": [ { "key": "auctions/v2/HYPERION/2021-03/000.msgpack.br", "etag": "<md5>", "size": 123 } ] } ] }
```

`objects` are the part blocks only (etag/size from the mirror manifest, i.e. the R2 ETag at mirror time). The tag index
object is not listed: it spans all months of the tag and changes whenever another month is added; its month entry is
represented by `parts`/`rows`/`partSha256`. `snapshot` is `""` until `snapshot.sh` stamps it with the short snapshot name
(`auction-archive-yyyymmddThhmmZ`; the dataset is `storage/backup`).

### 3. snapshot.sh

Dry run by default. With `--execute`: `zpool status -x storage` must say healthy, dataset mounted and key available,
then `zfs snapshot -r storage/backup@auction-archive-<UTC yyyymmddThhmmZ>` and `zfs hold -r auction-archive <snap>`
(automatic pruning can not remove it). It then `stat`s every object listed in the receipts inside
`<dataset mountpoint>/.zfs/snapshot/<snap>/...` (must exist with the receipt size) and MD5s EVERY one of them against the
receipt's etag (a multipart etag can not be verified and fails the run). Only if all of that passes are the receipts written
(stamped copies in `with-snapshot/`; the originals and any receipt in another directory are never touched). On failure the snapshot and its hold stay (release them by hand if unwanted:
`zfs release -r auction-archive <snap>`).

## What a receipt proves

* Every listed object existed in R2 with that ETag when it was mirrored, and the NAS file has exactly those bytes
  (MD5 == ETag, size equal) and decodes to a block whose recomputed SHA-256 equals the part hash recorded in the R2 tag
  index; part count and row count match the index. If `--mariadb-env` was used, the combined hash and rows also equal
  `archived_months`.
* The named held ZFS snapshot contains those files with those sizes and every one of them has the receipt's MD5, on a
  healthy pool, i.e. the data survives an accidental delete/overwrite in the live dataset.
* It does not prove that R2 still has the same objects later: a consumer must HEAD the parts at delete time and compare
  the ETags with `objects[].etag` (a privacy erasure rewrites parts, changes the ETag and therefore blocks deletion until the
  month is re-mirrored and re-receipted). It does not prove that the archive contains every Scylla/MariaDB row (that is the
  separate audit).

## After R2 changed (privacy erasure, re-archived month)

Erasures rewrite the affected parts (and the tag index) in R2. Re-run the whole chain: `mirror.py` (changed ETag, so it
re-downloads and overwrites the old file in the live dataset), `ArchiveNasVerify` (new receipts, the affected months get
new part hashes and ETags), `snapshot.sh --execute` (new snapshot, new stamped receipts). Receipts of unaffected months
are simply recreated and stay valid. Use `--month` on the verifier to limit the work.

## Snapshot rotation (privacy)

A held snapshot keeps the pre-erasure bytes of every part, so the NAS would keep personal data that was erased in R2.
Recommended policy:

1. Take a new snapshot after every erasure batch and at least monthly.
2. Only after the new snapshot passed `snapshot.sh` (stamped receipts exist) release the hold of the previous one:
   `zfs release -r auction-archive storage/backup@auction-archive-<old>` and `zfs destroy -r storage/backup@auction-archive-<old>`
   (optionally keep one older snapshot for a short, documented grace period).
3. Other snapshots of `storage/backup` (`services-daily-*`, `zfs-auto-snap_*`) also contain the mirror; they expire by their
   own retention, so the real erasure latency on the NAS is the longest of those retentions. `gone` entries in the
   manifest list objects R2 no longer has (the mirror keeps its old copy by design); review them after an erasure and delete
   such local files by hand if the erasure applies to them.
4. Deletion gates should reference only the newest receipt generation; older receipts become invalid when their hold is released.
