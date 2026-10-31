#!/usr/bin/env bash
# Take a held ZFS snapshot of the archive mirror, re-read it and stamp the backup receipts with the snapshot name.
#
#   ./snapshot.sh --receipts <verify output dir>                 # dry run (default): checks + plan, changes nothing
#   ./snapshot.sh --receipts <verify output dir> --execute       # snapshot, hold, verify, write receipts
#
# Options: --mirror DIR (default /storage/backup/hypixel/sky-auctions-archive)  --dataset NAME (default storage/backup)
#          --hold-tag TAG (default auction-archive)
# The receipts are read from --receipts (receipt-yyyy-MM.json written by ArchiveNasVerify) and the stamped copies are
# written to <receipts>/with-snapshot/. The originals and any receipt elsewhere are not modified.
# EVERY object named in those receipts is re-read through the snapshot and its MD5 compared with the receipt's etag (an R2 ETag of a
# single-part PUT is the MD5); a multipart etag can not be checked and fails the run. Only then the receipts are stamped.
set -euo pipefail

MIRROR=/storage/backup/hypixel/sky-auctions-archive
DATASET=storage/backup
HOLD_TAG=auction-archive
RECEIPTS=
EXECUTE=0

die() { echo "snapshot.sh: $*" >&2; exit "${2:-1}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --execute) EXECUTE=1 ;;
    --receipts) RECEIPTS=${2:?}; shift ;;
    --mirror) MIRROR=${2:?}; shift ;;
    --dataset) DATASET=${2:?}; shift ;;
    --hold-tag) HOLD_TAG=${2:?}; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "unknown argument $1" 2 ;;
  esac
  shift
done
[ -n "$RECEIPTS" ] || die "--receipts is required" 2
[ -d "$RECEIPTS" ] || die "receipts dir $RECEIPTS does not exist" 2
[ -d "$MIRROR" ] || die "mirror dir $MIRROR does not exist" 2
compgen -G "$RECEIPTS/receipt-*.json" >/dev/null || die "no receipt-*.json in $RECEIPTS (run ArchiveNasVerify first; failed months have no receipt)" 2

POOL=${DATASET%%/*}

# 1. pool health ("zpool status -x <pool>" prints "pool '<pool>' is healthy" when fine)
status=$(zpool status -x "$POOL" 2>&1) || die "zpool status failed: $status"
case "$status" in *"is healthy"*) ;; *) die "pool $POOL is not healthy: $status" ;; esac

# 2. dataset mounted and key available
props=$(zfs get -H -o property,value keystatus,mounted "$DATASET" 2>&1) || die "zfs get $DATASET failed: $props"
mounted=$(printf '%s\n' "$props" | awk -F'\t' '$1=="mounted"{print $2}')
keystatus=$(printf '%s\n' "$props" | awk -F'\t' '$1=="keystatus"{print $2}')
[ "$mounted" = yes ] || die "dataset $DATASET is not mounted"
case "$keystatus" in available|-) ;; *) die "dataset $DATASET key is not loaded (keystatus=$keystatus)" ;; esac

# 3. which dataset holds the mirror (storage/backup/hypixel may be a child dataset of its own)
real_mirror=$(readlink -f "$MIRROR")
best_name=; best_mp=
while IFS=$'\t' read -r name mp; do
  case "$mp" in /*) ;; *) continue ;; esac
  case "$real_mirror/" in "$mp"/*) [ "${#mp}" -gt "${#best_mp}" ] && { best_name=$name; best_mp=$mp; } ;; esac
done < <(zfs list -H -o name,mountpoint -r -t filesystem "$DATASET")
[ -n "$best_name" ] || die "$real_mirror is not inside a mounted filesystem of $DATASET"
rel=${real_mirror#"$best_mp"}; rel=${rel#/}
echo "mirror lives in dataset $best_name (mountpoint $best_mp), relative path '${rel:-.}'"
echo "datasets in scope (snapshot -r):"; zfs list -H -o name -r -t filesystem "$DATASET" | sed 's/^/  /'

SNAP_NAME="auction-archive-$(date -u +%Y%m%dT%H%MZ)"
SNAP="$DATASET@$SNAP_NAME"
SNAP_ROOT="$best_mp/.zfs/snapshot/$SNAP_NAME"
if zfs list -H -o name -t snapshot "$SNAP" >/dev/null 2>&1; then die "snapshot $SNAP already exists (wait a minute)"; fi
SUDO_HINT="needs: sudo zfs allow ekwav snapshot,hold,release,mount $DATASET"

if [ "$EXECUTE" -ne 1 ]; then
  echo "DRY RUN. With --execute this would:"
  echo "  zfs snapshot -r $SNAP        ($SUDO_HINT)"
  echo "  zfs hold -r $HOLD_TAG $SNAP"
  echo "  stat and MD5 EVERY object of $(ls "$RECEIPTS"/receipt-*.json | wc -l) receipt(s) in $SNAP_ROOT/${rel} against the receipt etags"
  echo "  write stamped receipts to $RECEIPTS/with-snapshot/ (snapshot = $SNAP_NAME)"
  exit 0
fi

# 4. snapshot + hold
zfs snapshot -r "$SNAP" || die "zfs snapshot failed ($SUDO_HINT)"
echo "created $SNAP"
zfs hold -r "$HOLD_TAG" "$SNAP" || die "zfs hold failed; snapshot $SNAP exists WITHOUT hold ($SUDO_HINT)"
echo "hold '$HOLD_TAG' placed on $SNAP (and descendants)"

# 5. re-read through the snapshot and stamp the receipts
python3 -I - "$SNAP_ROOT/$rel" "$RECEIPTS" "$SNAP_NAME" <<'PY'
import glob, hashlib, json, os, sys

snap_dir, receipts, snap_name = sys.argv[1], sys.argv[2], sys.argv[3]
if not os.path.isdir(snap_dir):
    sys.exit("snapshot directory %s not visible (mount permission? zfs allow ... mount)" % snap_dir)

errors = []
docs = {}
for path in sorted(glob.glob(os.path.join(receipts, "receipt-*.json"))):
    try:
        with open(path, encoding="utf-8") as fh:
            docs[path] = json.load(fh)
    except (OSError, ValueError) as ex:
        errors.append("%s: unreadable or not JSON (%s)" % (path, ex))
    else:
        if not isinstance(docs[path], dict):
            errors.append("%s: not a JSON object" % path)
            del docs[path]


def object_problem(o):
    """Why a receipt object can not be checked, None when it has a key and a size."""
    if not isinstance(o, dict):
        return "is not an object"
    if not isinstance(o.get("key"), str) or not o["key"]:
        return "has no key"
    if not isinstance(o.get("size"), int) or isinstance(o["size"], bool) or o["size"] < 0:
        return "has no valid size"
    return None


def tag_problem(t):
    """Why a receipt tag can not be read, None when it is an object with a list of objects."""
    if not isinstance(t, dict):
        return "is not an object"
    if not isinstance(t.get("objects"), list):
        return "has no objects list"
    return None


def collect_objects(path, doc, objects, errors):
    tags = doc.get("tags", [])
    if not isinstance(tags, list):
        errors.append("%s: tags is not a list" % path)
        return
    for tag in tags:
        problem = tag_problem(tag)
        if problem:
            errors.append("%s: a tag %s: %s" % (path, problem, json.dumps(tag)[:120]))
            continue
        for o in tag["objects"]:
            problem = object_problem(o)
            if problem:
                errors.append("%s: tag %s: an object %s: %s" % (path, tag.get("tag"), problem, json.dumps(o)[:120]))
                continue
            etag = str(o.get("etag", "")).strip().strip('"').lower()
            if o["key"] in objects and objects[o["key"]][1] != etag:
                errors.append("%s: receipts disagree on the etag of %s" % (path, o["key"]))
            objects[o["key"]] = (o["size"], etag)


objects = {}
for path, doc in docs.items():
    if doc.get("version") != 1:
        errors.append("%s: unsupported version" % path)
    collect_objects(path, doc, objects, errors)


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


for key, (size, etag) in sorted(objects.items()):
    p = os.path.join(snap_dir, *key.split("/"))
    try:
        got = os.stat(p).st_size
    except OSError:
        errors.append("missing in snapshot: %s" % key)
        continue
    if got != size:
        errors.append("size %d != receipt %d in snapshot: %s" % (got, size, key))
        continue
    if not etag or "-" in etag:
        errors.append("receipt etag '%s' of %s is not a plain MD5 and can not be verified" % (etag, key))
        continue
    if md5_of(p) != etag:
        errors.append("md5 mismatch in snapshot: %s" % key)
print("stat- and md5-checked all %d receipt objects in the snapshot" % len(objects))

if errors:
    for e in errors[:50]:
        print("ERROR " + e, file=sys.stderr)
    sys.exit("snapshot verification FAILED (%d problems); receipts NOT stamped. The snapshot and its hold still exist." % len(errors))

out = os.path.join(receipts, "with-snapshot")
os.makedirs(out, exist_ok=True)
for path, doc in docs.items():
    doc["snapshot"] = snap_name
    target = os.path.join(out, os.path.basename(path))
    with open(target + ".tmp", "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)
        fh.write("\n")
        fh.flush()
        os.fsync(fh.fileno())
    os.replace(target + ".tmp", target)
print("stamped %d receipt(s) with snapshot %s into %s" % (len(docs), snap_name, out))
PY
echo "done. Verify the hold with: zfs holds -r $SNAP"
