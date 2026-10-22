# SkyFlipper
Code that finds flipable items in Hypixel skyblock.
Powers the result of https://sky.coflnet.com/flipper 
 

Configuration can be found in `appsettings.json`.
You can overwrite it via Enviroment variables. 

> **Note**: the keys represent the JSON path of a value and `:` has to be replaced with `__`. eg `TOPICS:FLIP` becomes `TOPICS__FLIP`

## Auction archive

Closed months of MariaDB auctions are archived as immutable blobs in R2 (`AuctionArchiveJob`) and, once verified and old enough, deleted from MariaDB (`AuctionRetentionWorker`). Code in `Services/Archive/`, blob format and index tables in the core library (`dev/Archive/`).

**Blob layout** (bucket `S3:BUCKET`, Brotli compressed MessagePack, content type `application/x-msgpack+br`)
- `auctions/v2/{tag}/{yyyy-MM}/{part:D3}.msgpack.br`: `ArchivedAuctionBlock`, the auctions of one tag that ended in one UTC month, at most `MAX_ROWS_PER_PART` rows per part, raw item bytes, bids, enchantments, nbt lookups, coop and claimed bidders included.
- `auctions/v2/{tag}/index.msgpack.br`: `ArchivedTagIndex`, the verified months of the tag (parts, rows, sold rows, sha256 per part).

**Tables** (`ArchiveContext`, same database, own migrations history `__ArchiveMigrationsHistory`, created by the job on start):
- `auction_tags(id, tag)`, `auction_locations(uid → tag_id, month, part)`, `auction_bidders(bidder_id, end, uid, amount)`: permanent indexes, never deleted.
- `archived_months(tag_id, month, ...)`: one row per verified tag-month; `tag_id = 0` is the month sentinel (`archived_at`: every tag archived, `deleted_at`: MariaDB rows deleted). `month` is months since 2019-01.

**What is archived and how**
- No items service and no id-to-tag map is used. Every `Auctions` row carries its own `Tag`; `ItemId` is only the index key. Once per pass `SELECT DISTINCT ItemId FROM Auctions` (loose scan on `IX_Auctions_ItemId_End`) lists the ids; each id is paged by `(ItemId, End)` per month. If that query fails the pass logs an error and writes no sentinel.
- The tag of a block comes from the rows: the rows of an ItemId are grouped by `row.Tag`. Edge cases:
  - one ItemId with several tags: each tag gets its own blocks;
  - several ItemIds feeding the same tag-month (ItemId 0 rows carry real tags): part numbers of a tag-month run across all ItemIds in ascending ItemId order, so they land in distinct parts. A re-run after a crash repeats the same order and overwrites the same part keys with the same rows (no duplicate parts); the markers (`archived_months`, R2 tag index) and the sentinel are written only after all ItemIds are done, so a crashed month is redone from its start.
- A row with a null or empty `Tag` fails the month (the auction id is logged), no sentinel.
- The sentinel is written only when every distinct ItemId was paged and every part verified. The retention worker deletes row by row only if the row's tag has a verified `archived_months` row for the month (`index_rows = rows`) and the uid is in `auction_locations`; an unarchived ItemId therefore blocks the month.

**Config**
- `S3:ENABLED`, `S3:ENDPOINT`, `S3:REGION`, `S3:BUCKET`, `S3:ACCESS_KEY`, `S3:SECRET_KEY`, `S3:USE_PATH_STYLE` (same as SkyBazaar; with an endpoint the region is only used for signing), optional `S3:CACHE_SIZE_BYTES` (default 256 MB).
- `ARCHIVE:ENABLED`, `START_DELAY_SECONDS`, `CLOSED_MARGIN_DAYS` (a month is closed 14 days after it ends), `OLDEST_MONTH` (default 2019-06, SkyBlock launch; raise it as the backfill completes months), `MAX_ROWS_PER_PART`, `PAGE_SIZE`, `PAGE_PAUSE_MS`, `TAG_PAUSE_MS`, `MONTH_PAUSE_MS`, `IDLE_PAUSE_MINUTES`.
- Retention: `ARCHIVE:DELETE_MARIADB`, `WINDOW_YEARS`, `MAX_DELETE_MONTHS_PER_DAY`, `DELETE_BATCH_SIZE`, `DELETE_PAUSE_MS`, `MAX_BINLOG_GB`, `MAX_HISTORY_LIST_LENGTH`, `REQUIRE_HEALTH_CHECKS` (the gate needs `SHOW BINARY LOGS` and `information_schema.INNODB_METRICS`, i.e. the `BINLOG MONITOR`/`REPLICATION CLIENT` and `PROCESS` privileges; without them nothing is deleted while this is true).

**Rollout order**
1. `S3__*` and `ARCHIVE__ENABLED=true` (`DELETE_MARIADB` stays false): the job archives oldest month first. Watch `sky_indexer_archive_*` (`verify_failures` must stay 0, `months_total` grows) and spot check blobs and `archived_months`.
2. `ARCHIVE__DELETE_MARIADB=true` only after the blobs and index rows are checked: months older than `WINDOW_YEARS` whose sentinel and every tag row are verified with `index_rows = rows` are deleted in batches (children, then `Auctions`, then unreferenced `NbtData`), one month per day by default. Watch `sky_indexer_retention_*`, disk, binlog and replica.
3. `OPTIMIZE TABLE` is an operator step (smallest table first, only when the table's used size fits the free disk); the worker logs each table's `data_free` after a month.
