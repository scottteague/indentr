# Web Parity TODO

All desktop features have been ported to the web UI.

---

# Multi-User Hardening TODO

Indentr is intentionally trust-based and authentication-free (`DESIGN.md:5,14,596-604`).
The items below are gaps relative to a "normal" multi-user app, plus places where the
implementation falls short of even the stated trust model. Priorities reflect what
should be fixed before treating the app as genuinely multi-user-safe.

## CRITICAL

- [x] Add ownership/authorization checks to note write paths:
  - `NoteRepository.DeleteAsync` / `PermanentlyDeleteAsync` (`Indentr.Data/Repositories/NoteRepository.cs:198-206,232-240`) take only a note ID — any user can trash/hard-delete any note by UUID.
  - `NoteRepository.SaveAsync` (`:143-196`) lets any user save over another user's public note and flips `owner_id` without checking the caller is authorized to edit.
  - `NoteRepository.RestoreAsync` (`:222-230`) takes only `id` — any user can restore another user's trashed note.
- [x] Auth-gate and privacy-check the web endpoints (`Indentr.Web/Program.cs:30-89`):
  - `/api/attachments/{id:guid}` returns any attachment by UUID to anyone, regardless of parent note privacy.
  - `/api/export/{noteId:guid}` runs the subtree export as the configured profile user and can leak that user's private notes to anyone who guesses a UUID.
- [ ] Add user/ownership scoping to `KanbanRepository` (`Indentr.Data/Repositories/KanbanRepository.cs`). Boards/cards have no `is_private` equivalent and no user checks on update/delete/add/move — effectively public and mutable by all.
- [ ] Add user/privacy scoping to `IAttachmentStore` / `PostgresAttachmentStore` — `OpenReadAsync`, `ListForNoteAsync`, `DeleteAsync`, `StoreAsync` take only IDs and are unguarded.
- [ ] Encrypt DB credentials at rest. `config.json` stores `DatabaseConfig.Password` (and `RemoteDatabase.Password`) in plaintext (`Indentr.UI/Config/AppConfig.cs:24-31`, `ConfigManager.cs:49-54`). Use OS keyring (DPAI on Windows, Secret Service on Linux, Keychain on macOS) or at minimum protected file permissions.
- [ ] Enforce TLS on DB connections. `ConnectionStringBuilder` (`Indentr.Data/ConnectionStringBuilder.cs:5-19`) doesn't set `SslMode`. Add `SslMode=Require` (or Prepend/VerifyFull) for remote profiles.

## IMPORTANT

- [ ] Add real authentication: passwords (hashed), login screen, sessions, logout. Currently identity is just a username string in the profile config and is auto-created on startup (`UserRepository.GetOrCreateAsync`).
- [ ] Add user deletion / deactivation with proper note reassignment. No `DeleteAsync` exists, and `notes.owner_id`/`created_by` FKs have no `ON DELETE` clause (`Migrations/001_InitialSchema.sql:22`, `002_PrivacyAndPerUserRoot.sql:4`), so users cannot be removed. Need a reassign-to-admin / make-public-on-departure workflow.
- [ ] Add per-note sharing ACLs. `is_private` is binary (public-to-all or private-to-creator-only). No `note_shares` table, no "share with user X", no groups/teams.
- [ ] Add roles / admin user. Currently every user is equivalent; there's no privileged user who can manage others' content or administer the profile/DB.
- [ ] Add edit history / versioning / audit log. Only the current version + the most recent conflict sibling are kept. `sync_log` is a transient push queue deleted after each successful push (`SyncService.cs:678-686`) — not an audit trail of who edited what when.
- [ ] Add note ownership transfer between users (reassign `created_by`/`owner_id`). Currently `created_by` is documented as immutable (`DESIGN.md:635`, `SyncService.cs:1062`) and there's no UI or service method to transfer.
- [ ] Fix web `AppSession` multi-user identity. It's per-circuit but reuses the shared server-side config profile (`Indentr.Web/Services/AppSession.cs:45`), so all web visitors act as the same single user. Add per-browser identity.
- [ ] Re-check privacy on note reload in open windows. `NotesWindow.ReloadIfOpenAsync` (`Indentr.UI/Views/NotesWindow.axaml.cs:25`), `MainWindow.ReloadIfRootAsync`, `RecoveryWindow` (`:99`), and Web `NoteEditor.ReloadContentAsync` (`:496`) call `GetByIdAsync` without re-validating `IsPrivate` — a note that flips to private while a window is open keeps reloading content.
- [ ] Real-time concurrent editing. Currently only 10-minute sync + conflict notes (`MainWindow.axaml.cs:65`). Consider presence indicators and/or OT/CRDT for live multi-user editing.

## NICE-TO-HAVE

- [ ] Notifications / activity feed (e.g. "X edited your note", conflict notifications beyond the save-time modal).
- [ ] Comments and @mentions on notes (beyond `[text](note:UUID)` links).
- [ ] Soft-delete sync for users and scratchpads (currently explicitly excluded — `SyncService.cs:1326-1330`).
- [ ] Pagination on `UserRepository.GetAllAsync` (`:34-45`) for large installs.
- [ ] Rate limiting / brute-force protection on the web (moot until auth exists, but worth noting).
- [ ] Per-user DB credentials / Postgres-level isolation (currently all users on a profile share one Postgres role).