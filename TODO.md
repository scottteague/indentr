# TODO

## Local/remote database sync

Add the ability to keep a local database and sync to/from a shared remote database automatically.

Key design decisions and work items:

- **Soft deletes** — add `deleted_at TIMESTAMPTZ` to all synced tables (`notes`, `kanban_boards`, `kanban_columns`, `kanban_cards`, `scratchpads`, `attachments`); update all repository queries to filter deleted rows.
- **Change tracking** — add a `sync_log (id, table_name, row_id, operation, changed_at)` table written by DB triggers, so each side knows what changed since the last sync.
- **Sync target config** — extend `DatabaseProfile` with an optional remote sync target connection.
- **SyncService** — a service that applies the change log against the remote in the correct order (topological sort for note parent/child relationships).
- **Conflict resolution** — reuse the existing `[CONFLICT]` sibling-note pattern for content conflicts detected at sync time.
- **Attachments** — sync notes first; handle large-object attachment sync lazily or on demand (expensive to transfer).
- **UI** — manual "Sync Now" trigger first; background auto-sync as a follow-up.

See the design discussion in conversation history for a fuller breakdown of difficulty tiers.
