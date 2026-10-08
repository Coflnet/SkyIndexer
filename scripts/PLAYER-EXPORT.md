# Local player exports

This tool collects read-only SQL records for one Minecraft name or UUID and
creates a local ZIP with mode `0600`. It never mutates live data. Credentials
come from the existing Kubernetes application secret in memory; SSH sends a
protected temporary MariaDB configuration over stdin, with an EXIT cleanup
trap. Each SQL session uses a read-only transaction and a statement deadline.

```sh
python3 scripts/player-export.py export AnyMinecraftName --output /private/player.zip --cql --mcconnect
python3 scripts/player-export.py export --uuid 0123456789abcdef0123456789abcdef --output /private/player.zip
python3 scripts/player-export.py package /private/existing-collection --output /private/player-v1.zip
python3 scripts/player-export.py verify /private/player-v1.zip
python3 -m unittest discover -s scripts -p test_player_export.py
```

Optional `--mcconnect` collects target account links, verified users and challenges with non-target identities redacted; a fixed field allowlist excludes credentials.

The optional CQL collector uses the existing secured `~/programs/cqlsh`
Python environment, TLS certificate and credentials from `~/.cassandra`.
Configure infrastructure with `--ansible-root`, `--context`, `--namespace`,
`--secret`, `--ssh-host`, `--sql-container`, `--cql-host`, and `--cql-python`.
The Ansible root is found alongside an ancestor of the script. Existing
bastion connectivity must already be active. No OpenBao init authority is used.
CQL failures and omitted sources have explicit coverage; SQL collection
failures prevent ZIP creation. UUID-only exports omit name-keyed CQL data
when a current name was not supplied. Profile exports use profile IDs found
in the target's SQL auctions/bids and active profile history.

Older S3 archives are excluded and are never scanned. `--no-older-archives`
records the operator's assertion that none are available; without the flag,
archive availability remains unassessed. There is no S3 discovery dependency.
Session-keyed records cannot be found from a UUID alone. NBT binary remains
base64 without any external decoding helper. Other public HTTP copies and
logs are outside the repeatable collection scope.

The ZIP has one root folder. `format.json` carries
`schema: coflnet-player-export/v1`, canonical 32-character `playerUuid`, and
UTC `createdAtUtc`. This is the packaging time; original identity and source
collection timestamps are preserved. Existing JSONL field names and nested
bid records remain unchanged. Every JSONL also has a CSV representation;
nested objects are JSON cells and formula-like text is escaped in CSV only. CSVs contain private user data like the source.
`SHA256SUMS` lists every other file exactly once. Verification rejects unsafe
paths, symlinks, duplicate entries, missing or altered files, source SQL
failures, and inconsistent player, auction, bid or relation ownership.
Exactly one positive SQL player ID is required for v1. Required SQL dataset files must exist even when empty. ZIPs are created exclusively,
so an existing destination is never overwritten. Source directories and ZIPs
must remain in a private location. Packaging preserves all supplied collection
files; it does not discover additional data or sanitize a preexisting
collection's optional datasets. Review optional datasets before sharing.

Auction relation coverage is limited to the target UUID on auctions owned by
or bid on by that target. Queries use the existing indexed numeric auction
foreign keys in batches; no unindexed global UUID scan is performed.

`sky_auctions.weekly_auctions_2` auction snapshots are not collected. These
snapshots may retain player identities and serialized bids; this is an explicit
coverage gap that must be assessed before claiming complete erasure. No global
auction scan or blob enumeration is performed.

Persistent opt-out requests are exported from `PlayerOptOutRequests` using the
exact target `PlayerUuid` and all existing stored columns.
`sql/opt-out-requests.jsonl` is empty when the table is absent, with
`sql/metadata.json` recording `optOutRequests.available: false`. Collection
detects the table through the existing schema inventory and never creates it.
