# TODO

## Local/remote database sync

### Done
- [x] **Migration 005** — `sync_log` table, `sync_state` table, `fn_sync_log()` trigger function applied to all seven entity tables (`notes`, `scratchpads`, `users`, `attachments`, `kanban_boards`, `kanban_columns`, `kanban_cards`). `updated_at` added to `kanban_columns` and `kanban_cards` with auto-maintenance trigger.
- [x] **Config model** — `DatabaseProfile.RemoteDatabase: DatabaseConfig?` added to `AppConfig`. Null = sync disabled. JSON-serialisable; no migration of existing config files needed (field is just absent/null for existing profiles).

### Remaining

- [x] **`ISyncService` interface** (`Indentr.Core`) — `SyncOnceAsync()` returning a `SyncResult` (success, offline, failed + message), `GetLastSyncedAtAsync()`

- [x] **Profile editor — remote DB fields** (`Indentr.UI`) — add optional Remote Database section (host, port, db name, username, password) to `FirstRunWindow` / `ProfilePickerWindow`; collapsed/hidden by default when not configured. Include a **Test connection** button that attempts `OpenAsync` with a short timeout and reports success/failure inline. This goes in early so you have a real way to configure a remote without hand-editing the JSON.

- [x] **SyncService — connect + remote migration** (`Indentr.Data`) — open a connection to the remote using `RemoteDatabase` from the profile; if unreachable return `SyncResult.Offline` immediately. On first successful connect, run `DatabaseMigrator` against the remote to ensure its schema is current.

- [x] **SyncService — push phase, entities** — drain `sync_log` in `id` order, skipping attachments for now:
  - Upsert `users` referenced by notes before the notes themselves (FK ordering)
  - Upsert `notes`, `scratchpads`, `kanban_boards`, `kanban_columns`, `kanban_cards` via `INSERT … ON CONFLICT (id) DO UPDATE SET …`
  - DELETE: delete from remote, ignore if already gone
  - Delete each `sync_log` entry only after the remote confirms the operation

- [x] **Sync status bar + sync button** (`Indentr.UI`) — single-line strip at the bottom of `MainWindow`. Shows `Synced at HH:MM`, `Offline`, or `Sync failed: <short message>`. Hidden when no `RemoteDatabase` is configured. Include a button that triggers `SyncService.SyncOnceAsync()` immediately.

- [x] **SyncService — pull phase, upsert + conflict detection** — query remote for all entity rows with `updated_at > last_synced_at`; process users first, then notes, scratchpads, kanban hierarchy:
  - Row absent locally → insert it
  - Row present locally, unmodified since `last_synced_at` → update it
  - Row present locally, also modified since `last_synced_at` → conflict: keep local, create `[CONFLICT] title (by user on timestamp)` sibling

- [x] **SyncService — pull phase, remote-delete detection** — compare UUID sets per entity type between remote and local; a UUID present locally but absent remotely means a remote delete:
  - Local row unmodified since `last_synced_at` → delete locally
  - Local row modified since `last_synced_at` → local edit wins; push it back on next sync cycle

- [x] **SyncService — `sync_state` update + partial-failure recovery** — `UPDATE sync_state SET last_synced_at = NOW()` only after both push and pull complete successfully. If push succeeds but pull fails, `last_synced_at` is not advanced; safe to retry from the same point.

- [x] **Attachment push** — extend the push phase to handle `attachments` sync_log entries: read bytes via `lo_get(lo_oid)` locally, write via `lo_from_bytea(0, data)` on remote, upsert the `attachments` metadata row with the new remote OID. Files are always uploaded to keep the remote the one true copy.

- [x] **Auto-sync timer** (`Indentr.UI`) — fires every 10 minutes in the background when a remote is configured; calls `SyncService.SyncOnceAsync()` and updates the status bar.

- [x] **Shift+Ctrl+S** (`Indentr.UI`) — saves all open windows (root note + all open `NotesWindow`s + scratchpad) then runs a full sync cycle; wire up in `MainWindow`.

### Nice-to-haves / later
- [ ] Lazy attachment download — skip byte transfer on pull, download on first open; needs a "not yet downloaded" flag or a local LO stub
- [x] Deduplicate consecutive sync_log entries for the same entity (e.g. 10 rapid UPDATEs → collapse to one) to reduce push overhead
- [x] Handle clock skew between local and remote (NTP drift can cause `updated_at > last_synced_at` to miss rows)
- [x] Expose `last_synced_at` in the profile picker for visibility
