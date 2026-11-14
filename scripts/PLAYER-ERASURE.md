# Player data erasure runbook (internal operator)

Scope: numbered sellers/bidders in SQL (MariaDB), target relations on known auctions,
and the auction archive when both `ARCHIVE:ENABLED` and `S3:ENABLED` are true.
Scylla/Cassandra source tables and other services require separate cleanup. Exports
and generated plans contain personal data: keep them private under `out/`, which is
excluded from Git, Docker contexts and .NET build/publish items. Remove task exports
and verification artifacts when they are no longer needed; use synthetic data in tests.

What the SQL phase of `privacy erase` does, in one transaction: auctions and bids of the player stay
(economic data other users need) but get an anonymous identity; the coop-member and
claimed-bid relation rows naming the player and the `Players` row are deleted, profile ids
equal to the player's uuid on the loaded rows are cleared, and `Users.MinecraftUuid` is set
to null for every account linked to the player (the number is printed). The account row, its
email and `AgreementAcceptances` are kept deliberately (compliance). `privacy check` writes
nothing. Nothing else in `HypixelContext` is touched.

The archive phase runs before SQL deletion, after the live SQL check. It discovers
auctions through indexed numeric seller/bidder IDs and archive indexes; unmarked
Scylla-sourced archive parts are also checked by UUID. Avoid global raw UUID queries
on `Auctions`: `AuctioneerId` has no index and a full scan can time out.
Rewritten blobs are read back and verified, hashes updated, and only then are the
player's archive index rows deleted. Archive writes are not rolled back if the later
SQL phase fails; rerun the same command while its export still matches SQL.

Bid and profile references of the player on auctions outside the export scope are not indexed,
so they are not scanned for. They are removed lazily: whenever the indexer loads such an auction
(ingestion of an update, `Auctions/export`, `Auctions/reindex`) the opted-out player is scrubbed
from what was loaded and the change is saved. Coop-member and claimed-bid rows on auctions outside
the export scope are not loaded by those paths and stay in SQL.

## Steps

1. Opt out on the running indexer so ingestion stops writing the player and erasure is
   not undone. The indexer is the only writer; this persists the uuid in
   `PlayerOptOutRequests` (table and `PlayerOptOut` class live in the core library) and
   refreshes the indexer's in-memory set immediately. Other services (for example
   SkyAuctions, SkyFlipTracker) hold the same list in memory, loaded at startup and
   reloaded hourly, so they pick the opt-out up within an hour. They read the table with
   `DBReadOnlyConnection` (a read-only DB user), falling back to `DBConnection` when unset.
   Services without database access (SkyFlipTracker) load the same
   list from the indexer's `GET Player/optout` (JSON array of uuids, base url `INDEXER_BASE_URL`).
   The uuid must be 32 lowercase hex characters without dashes.

   ```sh
   curl -X POST https://INDEXER_HOST/Player/optout/PLAYER_UUID
   ```

2. Export the player's data and verify the ZIP. Optional `--cql` and `--mcconnect`
   collect additional source data but do not erase it. Do not pass
   `--no-older-archives` unless their absence has actually been established.

   ```sh
   install -d -m 700 out
   python3 scripts/player-export.py export AnyMinecraftName \
     --cql --mcconnect --output out/player.zip
   python3 scripts/player-export.py verify out/player.zip
   ```

3. Preview offline (no database access), then compare read-only against the live DB.
   `check` fails if the export no longer matches the database (changed, extra or
   missing rows, or an export built for another player) or the player is not opted out.

   ```sh
   dotnet build -c Release
   dotnet bin/Release/net10.0/SkyIndexer.dll privacy preview out/player.zip
   dotnet bin/Release/net10.0/SkyIndexer.dll privacy check out/player.zip
   ```

4. Confirm archive configuration before erasing. Both flags above must be enabled
   to include the archive; otherwise the command completes SQL erasure and reports
   that the archive was untouched. The command repeats the live comparison before
   applying. SQL failures roll back its transaction; archive work is resumable.

   ```sh
   dotnet bin/Release/net10.0/SkyIndexer.dll privacy erase out/player.zip
   ```

5. Verify against the primary database using the numeric player ID captured before
   erasure: the player row, original seller/bidder references, target relations on
   known auctions, linked `Users.MinecraftUuid` values and archive seller/bidder
   index rows should be absent. Keep the opt-out. Auction/bid values remain, their
   UUIDs use the anonymous prefix and their profile IDs are cleared. The background
   `Numberer` can assign an anonymous sentinel ID from the UUID's hexadecimal suffix,
   so a numeric ID need not remain zero. Check the archive completion summary; zero
   rewrites is valid when no matching archived auctions were found. A post-erasure
   `privacy check` of the original export is expected to fail because its player was deleted.

For offline cross-service planning, prepare exact exported Scylla keys and snapshot hashes:

```sh
python3 scripts/player-erasure-plan.py out/player.zip --output out/erasure-plan.json
```

This makes no database changes and implements no Scylla apply command. BazaarPro account
deletion is a separate operation through its loopback-only endpoint; see
[its runbook](https://github.com/Coflnet/BazaarPro-API#internal-account-deletion).

Run the binary with the ordinary indexer database configuration (`DBConnection` in the
environment or a protected `appsettings.json`), never in command arguments. In Kubernetes
use `kubectl exec` with the direct `dotnet SkyIndexer.dll privacy ...` invocation. The
tamper recheck only compares the archive with the live database; it is not a
cryptographic confirmation by the operator.
