# Indentr - Design Document

## Overview

Indentr is a note-taking application inspired by [Tomboy Notes](https://wiki.gnome.org/Apps/Tomboy). Notes are organized as an **N-Tree** data structure where any note can have unlimited children. The app is built for **multi-user** use with a trust-based identity model and conflict-safe persistence.

## Technology Stack

| Component   | Technology                        |
|-------------|-----------------------------------|
| Language    | C# / .NET 10                      |
| UI          | Avalonia 11 / Avalonia.AvaloniaEdit |
| Database    | PostgreSQL                        |
| Auth Model  | Trust-based (no login, user self-identifies) |

---

## Architecture

### Data Structure: N-Tree

All notes exist as one or more N-Trees — one per user. Each user has their own **root node**, displayed on the Main Form at startup. Each note can have **unlimited children**.

```
Root Note
├── Child Note A
│   ├── Grandchild A1
│   └── Grandchild A2
│       └── Great-Grandchild A2a
├── Child Note B
└── Child Note C
```

**Orphan notes** are notes that have no parent in the tree. These can arise from deletion of a parent or from conflict resolution. Orphans are managed via the Management Form. Only orphans that belong to the current user or are public are shown.

### Lazy Loading Strategy

- The tree **structure** (node IDs, parent-child relationships) is loaded on demand — children are fetched only when a node is expanded or opened.
- Note **content** is loaded only when a note is opened for viewing/editing.
- This minimizes database load and supports multi-user concurrency.

---

## Forms

### 1. Main Form

The entry point of the application. Displays the **root node** of the note tree.

| Area           | Description |
|----------------|-------------|
| Note Area      | An instance of the **NoteEditorControl** displaying the root note. |
| Scratchpad     | Opened via **File → Scratchpad** as a separate window. Per-user, persisted to DB. |
| Switch Profile | **File → Switch Profile…** opens the Profile Picker in manage mode. Switching saves all open notes and restarts the application. |

### 2. Notes Form

Opened when a user clicks an **in-app link** within any note. Displays a single note using the **NoteEditorControl**.

| Area     | Description |
|----------|-------------|
| Note Area | An instance of the **NoteEditorControl** showing the linked note. |
| Menu bar  | A **Note** menu containing a **Delete Note…** action. |

Each in-app link click opens a **new Notes Form window**.

#### Deleting a Note from the Notes Form

The Notes Form provides a **Note → Delete Note…** menu item. Selecting it:

1. Shows a confirmation dialog ("Move to Trash?").
2. On confirmation, soft-deletes the note (sets `deleted_at`). The note disappears from the tree immediately but is recoverable from the **Trash Window**.
3. Closes the Notes Form without saving.

The root note cannot be deleted via this menu.

### 3. Management Form

Used to manage **orphan notes** and browse the full tree. Has two views:

| View               | Description |
|--------------------|-------------|
| Orphan Notes View  | Lists all notes with no parent. User can select an orphan to delete or link. |
| Tree Browser View  | Displays the full note tree. Used to select a **target parent** when linking an orphan. |

The Management Form opens on the **Tree Browser** tab by default. It switches to the **Orphan Notes** tab on opening only when orphans are actually present.

**Workflow for linking an orphan:**
1. User selects an orphan from the Orphan Notes View.
2. User browses the Tree Browser View and selects a parent note.
3. Clicking **Insert Link in Parent Note** appends an in-app link (`[title](note:UUID)`) to the end of the parent note's content and saves it. The save mechanism detects the new link and sets the orphan's `parent_id` automatically.

**Deletion** of notes is also available from this form (confirmation dialog, moves to Trash).

### 4. Trash Window

Opened via **File → Trash…**. Displays all soft-deleted items in two tabs:

| Tab    | Contents |
|--------|----------|
| Notes  | Trashed notes, sorted by deletion time (most recent first). |
| Kanban | Trashed boards, columns (only those whose board is active), and cards (only those whose column and board are active), each in a separate list. |

Per-item actions:

| Button               | Behaviour |
|----------------------|-----------|
| **Restore**          | Clears `deleted_at` on the selected item (and, for boards/columns, on all of their descendants). The item reappears in its original location. |
| **Delete Permanently** | Confirmation dialog, then hard `DELETE`. For notes, DB cascades remove attachments (which triggers `trg_attachment_lo_cleanup` to unlink large objects). For boards, DB cascades remove columns and cards. |
| **Empty Trash**      | Permanently deletes everything in Trash, in safe FK order: cards → columns → boards → notes. |
| **Refresh**          | Reloads all lists from the database. |

### Window Behaviour

All Indentr windows are **independent peers** — no window is a child or owner of another. Any window can be brought to the front at any time without restriction.

---

## NoteEditorControl (Shared User Control)

The core editing component, reused across Main Form (note area), Main Form (scratchpad), and Notes Form.

### Editor Model: Markdown-Native

The editor works directly with **raw Markdown text**. The user can edit Markdown by hand or use toolbar buttons. Buttons simply insert/toggle Markdown syntax around the selected text.

The editor **renders** the Markdown with live visual styling so the user sees formatted output while editing raw Markdown source.

### Content Types

| Type          | Markdown Syntax |
|---------------|-----------------|
| Plain Text    | Raw text, no syntax. |
| Bullet Points | `- item` with indentation (`  - nested`). Infinitely nestable. |
| In-App Links  | `[link text](note:UUID)` — internal note reference. |
| Kanban Links  | `[link text](kanban:UUID)` — opens a kanban board window. |
| External Links| `[link text](https://...)` — standard URL. |

### Toolbar Buttons

| Button               | Markdown Applied          | Visual Rendering |
|----------------------|---------------------------|------------------|
| **Bold**             | `**selected text**`       | Bold text |
| **Red**              | `__selected text__`       | Red-colored text |
| **Italic**           | `*selected text*`         | Italic text |
| **Underline**        | `_selected text_`         | Underlined text |
| **Link**             | `[selected text](target)` | Clickable link (prompts for target) |
| **New Child Note**   | Creates a new note and inserts an in-app link | See below |
| **📋 Board**         | Creates a kanban board and inserts a kanban link | See [Kanban Boards](#kanban-boards) |
| **Export…**          | Exports current note as `.md` | — |
| **Export Subtree…**  | Exports note + all descendants to a folder | See [Export / Import](#export--import) |

### Attachment Bar

A fixed strip at the bottom of the NoteEditorControl, visible whenever a note is loaded (hidden for the scratchpad). It contains:

- A **📎 Attach** button that opens a multi-file picker. Each selected file is stored as a PostgreSQL large object and appears as a chip in the bar.
- One **chip per attachment** showing the filename. Clicking a chip opens the file with the OS default application. Right-clicking shows a context menu:

| Action      | Behaviour |
|-------------|-----------|
| **Open**    | Writes file to a temp path and launches with `Process.Start` / `UseShellExecute`. |
| **Save As…**| Opens a save-file picker; streams bytes to the chosen destination. |
| **Delete**  | Confirmation dialog, then soft-deletes the attachment (`deleted_at`). Permanently removed only when the parent note is permanently deleted from Trash. |

#### New Child Note Button

When the user selects text and clicks **New Child Note**:

1. A new note is created in the database as a child of the current note, with the selected text as its **title**.
2. The selected text is replaced with an in-app link: `[selected text](note:UUID)`.
3. A **Notes Form** opens displaying the new (empty) note, ready for editing.

If no text is selected, the button is disabled.

### Rendering Rules

| Markdown Syntax       | Rendered As |
|-----------------------|-------------|
| `**text**`            | **Bold** text |
| `__text__`            | Red-colored text |
| `*text*`              | Italic text |
| `_text_`              | Underlined text |
| `[text](note:UUID)`   | Clickable in-app note link (blue underline) |
| `[text](kanban:UUID)` | Clickable kanban board link (purple underline) |
| `[text](http...)`     | Clickable external link (darker blue underline) |

> **Key deviation from standard Markdown:** `__text__` renders as red, not bold. `_text_` renders as underlined, not italic.

### Link Behavior

| Condition                         | Action |
|-----------------------------------|--------|
| Link target starts with `http`    | Opens in the system's default external browser. |
| Link target starts with `note:`   | Opens a new **Notes Form** displaying the linked note. |
| Link target starts with `kanban:` | Opens the **Kanban Window** for the referenced board. If already open, brings it to the front. |

---

## Database Design (PostgreSQL)

### Tables

#### `users`

| Column       | Type          | Notes |
|--------------|---------------|-------|
| `id`         | `UUID` PK     | Auto-generated. |
| `username`   | `TEXT UNIQUE` | Trust-based identifier, user-provided. |
| `created_at` | `TIMESTAMPTZ` | Row creation time. |

#### `notes`

| Column         | Type               | Notes |
|----------------|--------------------|-------|
| `id`           | `UUID` PK          | Auto-generated. |
| `parent_id`    | `UUID` FK NULL     | References `notes.id`. NULL = orphan (except root). |
| `is_root`      | `BOOLEAN`          | TRUE for the user's personal root note. |
| `title`        | `TEXT`             | Note title. |
| `content`      | `TEXT`             | Raw Markdown source. |
| `content_hash` | `TEXT`             | SHA-256 hash of `content` for conflict detection. |
| `owner_id`     | `UUID` FK          | References `users.id`. The user who last edited. |
| `created_by`   | `UUID` FK          | References `users.id`. Immutable creator. |
| `is_private`   | `BOOLEAN`          | When TRUE, only the creator can view or edit. |
| `sort_order`   | `INTEGER`          | Ordering among siblings. |
| `created_at`   | `TIMESTAMPTZ`      | Row creation time. |
| `updated_at`   | `TIMESTAMPTZ`      | Last modification time. |
| `deleted_at`   | `TIMESTAMPTZ NULL` | Non-null when in the Trash. NULL = active. |
| `search_vector`| `TSVECTOR`         | Generated column for full-text search. |

#### `attachments`

| Column       | Type               | Notes |
|--------------|--------------------|-------|
| `id`         | `UUID` PK          | Auto-generated. |
| `note_id`    | `UUID` FK          | References `notes.id`. `ON DELETE CASCADE`. |
| `lo_oid`     | `OID`              | Reference into `pg_largeobject`. |
| `filename`   | `TEXT`             | Original filename. |
| `mime_type`  | `TEXT`             | MIME type (stored for future use). |
| `size`       | `BIGINT`           | File size in bytes. |
| `created_at` | `TIMESTAMPTZ`      | Row creation time. |
| `deleted_at` | `TIMESTAMPTZ NULL` | Non-null when soft-deleted. |

Large object cleanup is handled by the `trg_attachment_lo_cleanup` trigger (`BEFORE DELETE`), which calls `lo_unlink(OLD.lo_oid)`. This fires for hard DELETEs only; soft-delete preserves the large object so the file can be recovered on restore.

#### `kanban_boards`

| Column       | Type               | Notes |
|--------------|--------------------|-------|
| `id`         | `UUID` PK          | Auto-generated. |
| `title`      | `TEXT`             | Board display name. |
| `owner_id`   | `UUID` FK          | References `users.id`. `ON DELETE CASCADE`. |
| `created_at` | `TIMESTAMPTZ`      | Row creation time. |
| `updated_at` | `TIMESTAMPTZ`      | Last modification time. |
| `deleted_at` | `TIMESTAMPTZ NULL` | Non-null when in the Trash. |

#### `kanban_columns`

| Column       | Type               | Notes |
|--------------|--------------------|-------|
| `id`         | `UUID` PK          | Auto-generated. |
| `board_id`   | `UUID` FK          | References `kanban_boards.id`. `ON DELETE CASCADE`. |
| `title`      | `TEXT`             | Column header label. |
| `sort_order` | `INTEGER`          | Display order among the board's columns. |
| `deleted_at` | `TIMESTAMPTZ NULL` | Non-null when in the Trash. |

#### `kanban_cards`

| Column       | Type               | Notes |
|--------------|--------------------|-------|
| `id`         | `UUID` PK          | Auto-generated. |
| `column_id`  | `UUID` FK          | References `kanban_columns.id`. `ON DELETE CASCADE`. |
| `title`      | `TEXT`             | Card display text. |
| `note_id`    | `UUID` FK NULL     | References `notes.id`. `ON DELETE SET NULL`. |
| `sort_order` | `INTEGER`          | Display order within the column. |
| `created_at` | `TIMESTAMPTZ`      | Row creation time. |
| `deleted_at` | `TIMESTAMPTZ NULL` | Non-null when in the Trash. |

#### `scratchpads`

| Column         | Type          | Notes |
|----------------|---------------|-------|
| `id`           | `UUID` PK     | Auto-generated. |
| `user_id`      | `UUID` FK UNIQUE | References `users.id`. One per user. |
| `content`      | `TEXT`        | Raw Markdown source. |
| `content_hash` | `TEXT`        | Hash for conflict detection. |
| `updated_at`   | `TIMESTAMPTZ` | Last modification time. |

### Indexes

- `notes.parent_id` — for loading children of a node.
- `notes.search_vector` — GIN index for full-text search.
- `notes.(created_by) WHERE is_root = TRUE` — partial unique index; enforces one root per user.
- `attachments.note_id` — for loading all attachments belonging to a note.
- `kanban_columns.board_id` — for loading all columns of a board.
- `kanban_cards.column_id` — for loading all cards within a column.
- `notes(created_by, deleted_at) WHERE deleted_at IS NOT NULL` — partial index for Trash queries.
- `kanban_boards(owner_id, deleted_at) WHERE deleted_at IS NOT NULL` — partial index for Trash queries.
- `kanban_columns(board_id, deleted_at) WHERE deleted_at IS NOT NULL` — partial index for Trash queries.
- `kanban_cards(column_id, deleted_at) WHERE deleted_at IS NOT NULL` — partial index for Trash queries.
- `attachments(note_id, deleted_at) WHERE deleted_at IS NOT NULL` — partial index for Trash queries.

### Content Format (Raw Markdown)

The `content` field stores raw Markdown text. Example:

```markdown
This is a note with **bold text** and __red text__ and _underlined text_.

- First bullet
  - Nested bullet with a [link to another note](note:550e8400-e29b-41d4-a716-446655440000)
  - Nested bullet with an [external link](https://example.com)
```

---

## Multi-User Conflict Resolution

### Strategy: Hash-Based Optimistic Concurrency

1. **On load:** The client reads the note's `content` and its `content_hash`.
2. **On save:** The client sends the updated `content` along with the `content_hash` it originally loaded.
3. **Server checks:** If the stored hash matches the submitted hash, the save proceeds and a new hash is computed.
4. **Conflict detected:** If the hashes do not match (another user modified the note since it was loaded):
   - The **user's edits are saved** to the original note.
   - The **conflicting version** is preserved as a new sibling with a `⚠ CONFLICT:` title prefix.
   - The in-editor hash is updated so subsequent saves proceed normally.
   - The user is notified via a dialog.

### Conflict Note Behavior

- Appears as a sibling of the original note.
- The conflict note holds the **remote version**; the original retains the **user's edits**.
- Title format: `⚠ CONFLICT: <title>`.
- The user can open both notes and manually reconcile the content, then delete the conflict note.

---

## Application Flow

```
App Start → Loading screen shown
    │
    ▼
Profile selection:
  - Exactly 1 profile → used automatically (loading screen stays visible)
  - 0 or 2+ profiles  → loading screen hides, Profile Picker shown,
                         loading screen reappears after selection
    │
    ▼
DB schema migration (automatic)
    │
    ▼
Crash recovery scan (RecoveryWindow if unsaved files found)
    │
    ▼
Main Form loads root note and scratchpad; loading screen closes
    │
User clicks in-app link → New Notes Form opens (lazy fetch)
    │
User opens Management Form → orphan/tree view
```

---

## Note Titles

Titles are **user-editable**. When a note's title is saved, every other note that links to it (`[old text](note:UUID)`) has its link display text updated automatically. Any open window showing an affected note is reloaded from the database.

---

## Save Behavior

Notes and scratchpad content are saved explicitly — there is no auto-save on keystroke.

| Trigger                      | Description |
|------------------------------|-------------|
| `Ctrl+S`                     | Keyboard shortcut while the editor has focus. |
| **Save button**              | In the NoteEditorControl toolbar. |
| Form close / window exit     | Saving is attempted automatically on close. |
| **Clicking an in-app link**  | The current note is saved before the linked note is opened. |
| **Opening Manage Notes**     | The root note is saved before the Management Form opens. |
| **Insert Link in Parent**    | Any open window editing the parent note is saved before the link is appended, then reloaded. |
| **Shift+Ctrl+S**             | Saves all open editing surfaces. |

On save, the optimistic concurrency check is performed. If a conflict is detected, the user's edits are saved, the conflicting version is preserved as a sibling, and the user is notified.

---

## Search

Full-text search via a dedicated **Search Form**.

- Opened from a search button or menu entry on the Main Form.
- Results display note titles; clicking a result opens the note in a new **Notes Form**.
- Implemented using PostgreSQL `tsvector` / `tsquery` against the `search_vector` column.

---

## Undo / Redo

The **NoteEditorControl** supports **infinite undo/redo** per editing session.

- History is maintained **in-memory only** (not persisted).
- History resets when the note is closed.
- Shortcuts: `Ctrl+Z` (undo), `Ctrl+Y` or `Ctrl+Shift+Z` (redo).

---

## Export / Import

### Single-note export

The **Export…** button exports the current note as a `.md` file. In-app links (`[text](note:UUID)`) are stripped to plain text; external links and Markdown formatting are preserved as-is.

### Subtree export

The **Export Subtree…** button (`SubtreeExporter` in `Indentr.Data`) exports the current note and all of its descendants into a self-contained folder:

```
{NoteTitle}-export/
  manifest.json          — version, counts, root note ID
  notes/
    {title}-{id}.md      — YAML frontmatter + raw note content
  boards/
    {title}-{id}.json    — full board: columns → cards
  attachments/
    {id}.json            — sidecar: noteId, filename, mimeType
    {id}.bin             — raw attachment bytes
```

Note frontmatter is hand-parseable `key: value` pairs delimited by `---`. Kanban boards are collected by scanning all exported note content for `kanban:UUID` links.

### Subtree import

**File → Import…** (`SubtreeImporter` in `Indentr.Data`) reads an export folder and recreates everything under the current user with fresh IDs:

1. Validates `manifest.json` (version must be 1).
2. Topologically sorts notes (parents before children).
3. Mints new GUIDs for every note and board.
4. Creates notes, then boards (with columns and cards), then attachments.
5. Rewrites all `note:` and `kanban:` links in note content to point at the new IDs.

---

## Attachments

Any note (including the root note) can have one or more binary file attachments. Not available on the scratchpad.

### Storage: PostgreSQL Large Objects

File bytes are stored using PostgreSQL's built-in large object facility (`pg_largeobject`). The `attachments` table stores metadata and the `OID` reference.

| Operation   | SQL Function |
|-------------|-------------|
| Store file  | `lo_from_bytea(0, @data)` — creates the large object, returns OID |
| Read file   | `lo_get(lo_oid)` — returns full content as `bytea` |
| Delete file | `lo_unlink(lo_oid)` — called automatically by the DB trigger |

All large object function calls require an active transaction.

### Swappable Backend

Defined by the `IAttachmentStore` interface in `Indentr.Core`:

```csharp
Task<IReadOnlyList<AttachmentMeta>> ListForNoteAsync(Guid noteId);
Task<(AttachmentMeta Meta, Stream Content)?> OpenReadAsync(Guid attachmentId);
Task<AttachmentMeta> StoreAsync(Guid noteId, string filename, string mimeType, Stream content);
Task DeleteAsync(Guid attachmentId);
```

Current implementation: `PostgresAttachmentStore` in `Indentr.Data`.

---

## Kanban Boards

Any note can embed a link to a kanban board using the `kanban:UUID` link scheme. Boards are independent of the note tree.

### Creating a Board

Click the **📋 Board** button in the NoteEditorControl toolbar:

1. An input dialog prompts for the board title.
2. A new board is created in the database.
3. A `[title](kanban:UUID)` link is inserted at the cursor position.
4. The **Kanban Window** opens immediately.

### Kanban Window

| Area        | Description |
|-------------|-------------|
| Board title | Editable TextBox at the top. Saved on focus-out. |
| + Column    | Button in the top bar. Prompts for a title and appends a new column. |
| Hint bar    | One-line reminder of keyboard shortcuts. |
| Column area | Horizontally scrollable list of column panels. |

#### Columns

Each column panel contains an editable title, a scrollable card list, a **× delete** button, and a **+ Add Card** button.

#### Cards

- **Click** — selects the card.
- **Double-click** — opens the linked note. If no note is linked, creates one automatically.
- **Right-click** — context menu: Rename, Open Linked Note, Unlink Note, Link to Existing Note…, Create and Link New Note…, Delete Card.

#### Keyboard Navigation

| Key                       | Action |
|---------------------------|--------|
| `↑` / `↓`                 | Move selection up/down within column. |
| `←` / `→`                 | Move selection to adjacent column. |
| `Shift+↑` / `Shift+↓`    | Move card up/down within column. Persisted immediately. |
| `Shift+←` / `Shift+→`    | Move card to adjacent column. Persisted immediately. |
| `F2` or `Enter`           | Rename the selected card. |
| `Delete`                  | Delete the selected card (with confirmation). |

### Data Model

Sort order is maintained as an integer `sort_order` column. After any move, the affected column(s) are fully renumbered in a batch update. Cards reference notes via a nullable `note_id` FK with `ON DELETE SET NULL`.

---

## Configuration File

Stored at `~/.config/indentr/config.json`. Created automatically on first launch.

### Schema

```json
{
  "lastProfile": "Personal",
  "profiles": [
    {
      "name": "Personal",
      "username": "alice",
      "localSchemaId": "abc123",
      "database": {
        "host": "localhost",
        "port": 5432,
        "name": "indentr",
        "username": "postgres",
        "password": ""
      }
    }
  ]
}
```

`lastProfile` records the most recently used profile so it can be pre-selected on next launch. `localSchemaId` scopes the PostgreSQL search path to `indentr_<id>`, allowing multiple profiles to share a single PostgreSQL database without table conflicts.

### Legacy Migration

Older installs stored `username` and `database` at the top level of `config.json`. On first load, `ConfigManager` detects this, wraps the existing settings into a profile named **"Default"**, and re-saves in the new format.

---

## Profile Picker

The **Profile Picker** (`ProfilePickerWindow`) handles both startup profile selection and in-app profile management. It is the same window in both contexts; only the action button label differs.

| Mode    | Trigger                      | Action button      |
|---------|------------------------------|--------------------|
| Startup | 0 or 2+ profiles at launch   | **Open**           |
| Manage  | File → Switch Profile…       | **Switch & Restart** |

- **Add** — opens `FirstRunWindow` as a modal dialog to create a new profile.
- **Edit** — opens `FirstRunWindow` pre-filled with the selected profile's settings.
- **Delete** — confirmation dialog, then removes the profile.
- **Open / Switch & Restart** — saves selection to `lastProfile`, then proceeds with startup or restarts the app.

### First-ever launch (0 profiles)

The picker opens with an empty list and immediately triggers the Add dialog. If the user cancels, the app shuts down.

---

## First-Run & Database Initialization

On startup, in order:

1. **Profile selection** — Config loaded from `~/.config/indentr/config.json`.
   - **No profiles**: Profile Picker opens and prompts to add a profile. App does not proceed until one exists.
   - **Exactly one profile**: used automatically.
   - **Two or more profiles**: Profile Picker shown; last-used profile is pre-selected.
2. **Database schema** — Pending migrations run automatically. If the database is unreachable, the user is shown an error and startup aborts.
3. **Crash recovery** — `RecoveryManager` scans `~/.config/indentr/recovery/` for unsaved note files left by a previous crash. If any are found, `RecoveryWindow` is shown so the user can restore or discard each one.
4. **Root note** — If none exists for the current user (`is_root = TRUE AND created_by = userId`), one is created with the title "Root".
5. **Scratchpad** — If none exists for the current user, one is created (empty content).

---

## User Identification

Trust-based, no authentication.

1. Each **profile** carries its own username. On first launch the user is prompted to provide one.
2. No password required.
3. If the username does not exist in the `users` table, a new row is created automatically on startup.
4. Different profiles can use different usernames, allowing a single install to act as different identities against different databases.

---

## Note Deletion Behavior

When a parent note is **deleted**, its children become **orphans** (`parent_id` set to NULL by the link-derivation logic on the next save).

### Single Source of Truth: In-App Links

`parent_id` is always derived from in-app links — never set directly from the UI. Every time a note is saved, `SyncParentLinksAsync` reconciles `parent_id` with the link graph:

- **Link added**: if the referenced note is currently orphaned, its `parent_id` is set to the note being saved.
- **Link removed**: if no note contains a link to a UUID, that note's `parent_id` is cleared.

The Management Form's "Insert Link in Parent Note" works by appending a real text link to the parent's content — adoption happens as a side-effect of the normal save.

- Children are **not** cascade deleted.
- Orphaned notes appear in **Management Form → Orphan Notes View**.

---

## Per-User Roots and Privacy

### Per-User Root Notes

Each user gets their own personal root note on first login. Identified by `is_root = TRUE AND created_by = userId`. Enforced by a partial unique index.

### Note Privacy

| Field        | Description |
|--------------|-------------|
| `created_by` | Immutable creator of the note. |
| `is_private` | When TRUE, only `created_by` can view or edit. Default: FALSE. |

Visibility is enforced in:
- `GetChildrenAsync`, `GetOrphansAsync`, `SearchAsync`: SQL filters by `is_private = FALSE OR created_by = @userId`.
- `NotesWindow.OpenAsync`: hard block — opening another user's private note shows an error.

A **Public** checkbox appears in the NoteEditorControl toolbar for all regular notes (hidden for root and scratchpad). Unchecking makes the note private on the next save.

#### Privacy Mismatch Warning

When linking a private orphan into a public parent note via the Management Form, a confirmation dialog warns the user that the link makes the private note reachable by anyone who can read the parent.

---

## Scratchpad Workflow

The scratchpad is a **user-managed workspace**. Moving content into the note tree is done manually by the user (copy/paste).

---

## Attachments Storage Detail

See [Attachments](#attachments) above. Large objects require explicit transaction management — each repository method opens a transaction before any `lo_*` call and commits after.

---

## Project Structure

```
Indentr.sln
├── Indentr.Core/    — Domain models, interfaces, business logic
├── Indentr.Data/    — PostgreSQL repositories, migrations
├── Indentr.UI/      — Avalonia app, windows, controls, config
└── Indentr.Tests/   — Unit and integration tests
```

| Project         | Responsibilities |
|-----------------|------------------|
| `Indentr.Core`  | Note, User, Scratchpad, AttachmentMeta, Kanban models; repository interfaces; conflict resolution |
| `Indentr.Data`  | Npgsql-based repository implementations; schema migrations (`DatabaseMigrator`); `SubtreeExporter`; `SubtreeImporter` |
| `Indentr.UI`    | Avalonia App, all windows and controls, config management, recovery manager |
| `Indentr.Tests` | Unit tests (mocked); integration tests via Testcontainers (real Postgres, covers full export/import round-trip) |

Dependency direction: `UI → Data → Core`. Core has zero external dependencies.

---

## Container Setup (Docker / Podman)

A `docker-compose.yml` is provided at the project root for running PostgreSQL locally.

```sh
cp .env.example .env          # configure data directory
podman-compose up -d          # or: docker compose up -d
podman exec -it indentr-db psql -U postgres -c "CREATE DATABASE indentr;"
```

PostgreSQL data is stored in a bind mount configured via `INDENTR_DATA_DIR` in `.env` (defaults to `./data`).
