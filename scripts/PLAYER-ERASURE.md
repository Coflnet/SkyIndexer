# Player data erasure runbook (internal operator)

Scope: SQL (MariaDB) only. This command does **not** erase data in Scylla/Cassandra or
in any other service; handle those separately. Exports contain personal data: keep them
private (`out/` is ignored by Git).

What `privacy erase` does, in one transaction: auctions and bids of the player stay
(economic data other users need) but get an anonymous identity; the coop-member and
claimed-bid relation rows naming the player and the `Players` row are deleted, profile ids
equal to the player's uuid on the loaded rows are cleared, and `Users.MinecraftUuid` is set
to null for every account linked to the player (the number is printed). The account row, its
email and `AgreementAcceptances` are kept deliberately (compliance). `privacy check` writes
nothing. Nothing else in `HypixelContext` is touched.

Coop-member, claimed-bid and profile references of the player on auctions outside the export
scope are not indexed, so they are not scanned for. They are removed lazily: whenever the
indexer loads such an auction (ingestion of an update, `Auctions/export`, `Auctions/reindex`)
the opted-out player is scrubbed from it and the change is saved.

## Steps

1. Opt out on the running indexer so ingestion stops writing the player and erasure is
   not undone. The indexer is the only writer; this persists the uuid in
   `PlayerOptOutRequests` (table and `PlayerOptOut` class live in the core library) and
   refreshes the indexer's in-memory set immediately. Other services (for example
   SkyAuctions, SkyFlipTracker) hold the same list in memory, loaded at startup and
   reloaded hourly, so they pick the opt-out up within an hour. They read the table with
   `DBReadOnlyConnection` (a read-only DB user), falling back to `DBConnection` when unset.
   The uuid must be 32 lowercase hex characters without dashes.

   ```sh
   curl -X POST https://INDEXER_HOST/Player/optout/PLAYER_UUID
   ```

2. Export the player's data and verify the archive.

   ```sh
   mkdir -m 700 -p out
   python3 scripts/player-export.py export AnyMinecraftName \
     --cql --mcconnect --no-older-archives --output out/player.zip
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

4. Erase. Run the same comparison again, then apply; any failure rolls back.

   ```sh
   dotnet bin/Release/net10.0/SkyIndexer.dll privacy erase out/player.zip
   ```

Run the binary with the ordinary indexer database configuration (`DBConnection` in the
environment or a protected `appsettings.json`), never in command arguments. In Kubernetes
use `kubectl exec` with the direct `dotnet SkyIndexer.dll privacy ...` invocation. The
tamper recheck only compares the archive with the live database; it is not a
cryptographic confirmation by the operator.
