# Organiz - Design Document

## Overview

Organiz is a note-taking application inspired by [Tomboy Notes](https://wiki.gnome.org/Apps/Tomboy). Notes are organized as an **N-Tree** data structure where any note can have unlimited children. The app is built for **multi-user** use with a trust-based identity model and conflict-safe persistence.

## Technology Stack

| Component   | Technology                        |
|-------------|-----------------------------------|
| Language    | C# / .NET 10 (SDK 10.0.103)       |
| UI          | Avalonia 11.3 / Avalonia.AvaloniaEdit 11.4 |
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

| Area        | Description |
|-------------|-------------|
| Note Area   | An instance of the **NoteEditorControl** (shared user control) displaying the root note. |
| Scratchpad  | Opened via **File → Scratchpad** as a separate window. Per-user, persisted to DB. Content may later be moved into the tree manually. |
| Switch Profile | **File → Switch Profile…** opens the Profile Picker in manage mode. The user can add, edit, or delete profiles, or select a different one to switch to. Switching saves all open notes and restarts the application with the chosen profile. |

### 2. Notes Form

Opened when a user clicks an **in-app link** within any note. Displays a single note using the **NoteEditorControl**.

| Area        | Description |
|-------------|-------------|
| Note Area   | An instance of the **NoteEditorControl** showing the linked note. |
| Menu bar    | A **Note** menu containing a **Delete Note…** action. |

Each in-app link click opens a **new Notes Form window**.

#### Deleting a Note from the Notes Form

The Notes Form provides a **Note → Delete Note…** menu item. Selecting it:

1. Shows a confirmation dialog ("Are you sure?").
2. On confirmation, deletes the note (children become orphans — same behaviour as deletion from the Management Form).
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

**Deletion** of notes is also available from this form (with confirmation dialog).

### Window Behaviour

All Organiz windows (Main Form, Notes Form, Search Form, Management Form) are **independent peers** — no window is a child or owner of another. Any window can be brought to the front at any time without restriction.

---

## NoteEditorControl (Shared User Control)

The core editing component, reused across Main Form (note area), Main Form (scratchpad), and Notes Form.

### Editor Model: Markdown-Native

The editor works directly with **raw Markdown text**. The user can edit Markdown by hand or use toolbar buttons. Buttons simply insert/toggle Markdown syntax around the selected text — they do not use a separate rich-text model.

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

Each button wraps/unwraps the selected text with Markdown syntax. Formatting is **combinable**.

| Button           | Markdown Applied          | Visual Rendering |
|------------------|---------------------------|------------------|
| **Bold**         | `**selected text**`       | Bold text |
| **Red**          | `__selected text__`       | Red-colored text |
| **Italic**       | `*selected text*`         | Italic text |
| **Underline**    | `_selected text_`         | Underlined text |
| **Link**         | `[selected text](target)` | Clickable link (prompts for target) |
| **New Child Note** | Creates a new note and inserts an in-app link | See below |
| **📋 Board**     | Creates a kanban board and inserts a kanban link | See [Kanban Boards](#kanban-boards) |

### Attachment Bar

A fixed strip at the bottom of the NoteEditorControl, visible whenever a note is loaded (hidden for the scratchpad). It contains:

- A **📎 Attach** button that opens a multi-file picker. Each selected file is stored as a PostgreSQL large object and appears immediately as a chip in the bar.
- One **chip per attachment** showing the filename. Clicking a chip opens the file with the OS default application (written to a temp path first). Right-clicking shows a context menu:

| Action      | Behaviour |
|-------------|-----------|
| **Open**    | Writes file to a system temp path and launches with `Process.Start` / `UseShellExecute`. |
| **Save As…**| Opens a save-file picker; streams bytes directly to the chosen destination. |
| **Delete**  | Confirmation dialog, then removes the attachment from the database permanently. |

#### New Child Note Button

When the user selects text and clicks **New Child Note**:

1. A new note is created in the database as a child of the **currently displayed note**, with the selected text used as its **title**.
2. The selected text in the editor is replaced with an in-app link: `[selected text](note:UUID)` pointing to the new note.
3. A **Notes Form** opens displaying the new (empty) note, ready for editing.

If no text is selected, the button is disabled.

**Bold + Red example:** `**__text__**` — renders as bold and red simultaneously.

### Rendering Rules

The editor applies custom rendering on top of standard Markdown:

| Markdown Syntax       | Rendered As |
|-----------------------|-------------|
| `**text**` (asterisks)| **Bold** text |
| `__text__` (underscores)| **Red-colored** text (not bold) |
| `*text*`        | italic text |
| `_text_`        | Underlined text (not italic) |
| `[text](note:UUID)`   | Clickable in-app note link (blue underline) |
| `[text](kanban:UUID)` | Clickable kanban board link (purple underline) |
| `[text](http...)`    | Clickable external link (darker blue underline) |

> **Key deviation from standard Markdown:** `__text__` is rendered as red, not bold. This is an intentional Organiz-specific rendering rule.

### Link Behavior

| Condition                          | Action |
|------------------------------------|--------|
| Link target starts with `http`     | Opens in the system's default external browser. |
| Link target starts with `note:`    | Opens a new **Notes Form** displaying the linked note (by UUID). |
| Link target starts with `kanban:`  | Opens the **Kanban Window** for the referenced board (by UUID). If the window is already open, it is brought to the front. |

---

## Database Design (PostgreSQL)

### Tables

#### `users`

| Column       | Type         | Notes |
|--------------|--------------|-------|
| `id`         | `UUID` PK    | Auto-generated. |
| `username`   | `TEXT UNIQUE` | Trust-based identifier, user-provided. |
| `created_at` | `TIMESTAMPTZ` | Row creation time. |

#### `notes`

| Column         | Type           | Notes |
|----------------|----------------|-------|
| `id`           | `UUID` PK      | Auto-generated. |
| `parent_id`    | `UUID` FK NULL  | References `notes.id`. NULL = orphan (except root). |
| `is_root`      | `BOOLEAN`       | TRUE for the user's personal root note. |
| `title`        | `TEXT`          | Note title. |
| `content`      | `TEXT`          | Raw Markdown source. |
| `content_hash` | `TEXT`          | Hash of `content` for conflict detection. |
| `owner_id`     | `UUID` FK       | References `users.id`. The user who last edited. |
| `created_by`   | `UUID` FK       | References `users.id`. Immutable creator of the note. |
| `is_private`   | `BOOLEAN`       | When TRUE, only the creator can view or open this note. |
| `sort_order`   | `INTEGER`       | Ordering among siblings. |
| `created_at`   | `TIMESTAMPTZ`   | Row creation time. |
| `updated_at`   | `TIMESTAMPTZ`   | Last modification time. |
| `search_vector`| `TSVECTOR`      | Generated column for full-text search. |

#### `attachments`

| Column       | Type           | Notes |
|--------------|----------------|-------|
| `id`         | `UUID` PK      | Auto-generated. |
| `note_id`    | `UUID` FK      | References `notes.id`. `ON DELETE CASCADE` — attachments are removed with their note. |
| `lo_oid`     | `OID`          | Reference into `pg_largeobject` where the file bytes are stored. |
| `filename`   | `TEXT`         | Original filename as provided by the user. |
| `mime_type`  | `TEXT`         | MIME type (currently always `application/octet-stream`; stored for future use). |
| `size`       | `BIGINT`       | File size in bytes at the time of upload. |
| `created_at` | `TIMESTAMPTZ`  | Row creation time. |

Large object cleanup is handled by the `trg_attachment_lo_cleanup` trigger (`BEFORE DELETE`), which calls `lo_unlink(OLD.lo_oid)`. This fires for both explicit deletes and cascades from note deletion, preventing orphaned large objects in `pg_largeobject`.

#### `kanban_boards`

| Column       | Type           | Notes |
|--------------|----------------|-------|
| `id`         | `UUID` PK      | Auto-generated. |
| `title`      | `TEXT`         | Board display name. |
| `owner_id`   | `UUID` FK      | References `users.id`. `ON DELETE CASCADE`. |
| `created_at` | `TIMESTAMPTZ`  | Row creation time. |
| `updated_at` | `TIMESTAMPTZ`  | Last modification time. |

#### `kanban_columns`

| Column       | Type           | Notes |
|--------------|----------------|-------|
| `id`         | `UUID` PK      | Auto-generated. |
| `board_id`   | `UUID` FK      | References `kanban_boards.id`. `ON DELETE CASCADE`. |
| `title`      | `TEXT`         | Column header label. |
| `sort_order` | `INTEGER`      | Display order among the board's columns. |

#### `kanban_cards`

| Column       | Type           | Notes |
|--------------|----------------|-------|
| `id`         | `UUID` PK      | Auto-generated. |
| `column_id`  | `UUID` FK      | References `kanban_columns.id`. `ON DELETE CASCADE`. |
| `title`      | `TEXT`         | Card display text. |
| `note_id`    | `UUID` FK NULL | References `notes.id`. `ON DELETE SET NULL` — the card survives if its linked note is deleted. |
| `sort_order` | `INTEGER`      | Display order within the column. |
| `created_at` | `TIMESTAMPTZ`  | Row creation time. |

#### `scratchpads`

| Column         | Type           | Notes |
|----------------|----------------|-------|
| `id`           | `UUID` PK      | Auto-generated. |
| `user_id`      | `UUID` FK UNIQUE` | References `users.id`. One per user. |
| `content`      | `TEXT`          | Raw Markdown source. Same format as `notes.content`. |
| `content_hash` | `TEXT`          | Hash for conflict detection. |
| `updated_at`   | `TIMESTAMPTZ`   | Last modification time. |

### Indexes

- `notes.parent_id` — for loading children of a node.
- `notes.search_vector` — GIN index for full-text search.
- `notes.(created_by) WHERE is_root = TRUE` — partial unique index; enforces one root per user (replaces the old single-root index).
- `attachments.note_id` — for loading all attachments belonging to a note.
- `kanban_columns.board_id` — for loading all columns of a board.
- `kanban_cards.column_id` — for loading all cards within a column.

### Content Format (Raw Markdown)

The `content` field stores raw Markdown text. Example:

```markdown
This is a note with **bold text** and __red text__ and _underlined text_.

Here is a **__bold and red__** word.

- First bullet
  - Nested bullet with a [link to another note](note:550e8400-e29b-41d4-a716-446655440000)
  - Nested bullet with an [external link](https://example.com)
    - Deeply nested bullet
```

This is stored as-is in the `TEXT` column. No transformation needed for export — the content is already valid Markdown (with Organiz-specific rendering of `__text__` as red).

---

## Multi-User Conflict Resolution

### Problem

Multiple users may edit the same note concurrently. The system must avoid data loss.

### Strategy: Hash-Based Optimistic Concurrency

1. **On load:** The client reads the note's `content` and its `content_hash`.
2. **On save:** The client sends the updated `content` along with the `content_hash` it originally loaded.
3. **Server checks:** If the stored `content_hash` matches the submitted hash, the save proceeds normally and a new hash is computed.
4. **Conflict detected:** If the hashes do not match (another user modified the note since it was loaded):
   - The updated content is **not** written over the existing note.
   - Instead, a **new conflict note** is created as a **sibling** (same `parent_id` as the original).
   - The conflict note is clearly marked (e.g., title prefix: `[CONFLICT] Original Title`) so the user can identify and manually merge it later.

### Conflict Note Behavior

- Appears as a sibling of the original note, making it visually obvious in the tree.
- The user can open both notes side by side and manually reconcile the content.
- After merging, the user deletes the conflict note.

---

## Application Flow

```
App Start
    │
    ▼
Main Form loads
    │
    ├── Fetches root note (lazy: structure + content for root only)
    │   └── NoteEditorControl displays root note
    │
    ├── Fetches user's scratchpad
    │   └── NoteEditorControl displays scratchpad
    │
    ▼
User clicks an in-app link
    │
    ▼
New Notes Form opens
    │
    └── Fetches linked note content (lazy)
        └── NoteEditorControl displays note
            │
            ▼
        User clicks another link → another Notes Form opens (recursive)

User opens Management Form
    │
    ├── Orphan Notes View: fetches notes where parent_id IS NULL AND is_root = FALSE
    │
    └── Tree Browser View: fetches tree structure lazily for parent selection
```

---

## Note Titles

Titles are **user-editable**. Each note has a dedicated title field separate from content.

When a note's title is saved, every other note that contains an in-app link to it (`[old text](note:UUID)`) has its link display text updated to the new title automatically. Any open window showing an affected note is reloaded from the database so it stays in sync.

---

## Save Behavior

Notes and scratchpad content are saved explicitly — there is no auto-save on keystroke.

A save is triggered by any of the following:

| Trigger                   | Description |
|---------------------------|-------------|
| `Ctrl+S`                  | Keyboard shortcut while the editor has focus. |
| **Save button**           | A Save button in the NoteEditorControl toolbar. |
| Form close / window exit  | Saving is attempted automatically when a Notes Form or the Main Form is closed. |
| **Clicking an in-app link** | The current note is saved before the linked note is opened. |
| **Opening Manage Notes**  | The root note is saved before the Management Form opens. |
| **Insert Link in Parent** | Any open window editing the parent note is saved before the link is appended, then reloaded afterwards so its hash stays current. |

On save, the optimistic concurrency check (hash comparison) is performed. If a conflict is detected, the conflict note is created and the user is notified.

---

## Search

Full-text search is available via a dedicated **Search Form**.

- Opened from a search button or menu entry accessible from the Main Form.
- Contains a text input and a results list.
- Results display note titles; clicking a result opens the note in a new **Notes Form**.
- Search is implemented using PostgreSQL `tsvector` / `tsquery` against the `search_vector` column on the `notes` table.

---

## Undo / Redo

The **NoteEditorControl** supports **infinite undo/redo** per editing session.

- Undo/redo history is maintained **in-memory only** (not persisted).
- History resets when the note is closed or the form is closed.
- Standard keyboard shortcuts: `Ctrl+Z` (undo), `Ctrl+Y` or `Ctrl+Shift+Z` (redo).

---

## Drag and Drop

Notes can be **rearranged in the tree via drag and drop**.

- A note can be dragged to a new parent or reordered among siblings.
- Updates `parent_id` and `sort_order` in the database.
- Drag and drop is available in:
  - The **Tree Browser View** in the Management Form.
  - Any future tree-view component added to other forms.

---

## Export

Notes can be **exported to Markdown** (`.md` files).

- Content is already stored as raw Markdown, so export is near-zero transformation.
- Export a single note, or a subtree (note + all descendants).
- **In-app links** (`[text](note:UUID)`) are converted to plain text on export (since targets may not exist outside Organiz).
- **External links**, **bold**, **red** (`__text__`), **underline**, and **bullets** are already valid Markdown and exported as-is.
- Subtree export concatenates notes with their titles as headings, maintaining hierarchy via heading levels.

---

## Attachments

Any note (including the root note) can have one or more binary file attachments. Attachments are not available on the scratchpad.

### Storage: PostgreSQL Large Objects

File bytes are stored using PostgreSQL's built-in large object facility (`pg_largeobject`). The `attachments` table stores metadata and the `OID` reference; the actual bytes live outside the normal table heap, avoiding bloat on the `notes` table.

Operations use the PostgreSQL functions directly (not the deprecated Npgsql `NpgsqlLargeObjectManager`):

| Operation | SQL Function |
|-----------|-------------|
| Store file | `lo_from_bytea(0, @data)` — creates the large object, returns OID |
| Read file  | `lo_get(lo_oid)` — returns the full content as `bytea` |
| Delete file | `lo_unlink(lo_oid)` — called automatically by the DB trigger |

All large object function calls require an active transaction, which each repository method opens explicitly.

### Swappable Backend

The storage layer is defined by the `IAttachmentStore` interface in `Organiz.Core`:

```csharp
Task<IReadOnlyList<AttachmentMeta>> ListForNoteAsync(Guid noteId);
Task<(AttachmentMeta Meta, Stream Content)?> OpenReadAsync(Guid attachmentId);
Task<AttachmentMeta> StoreAsync(Guid noteId, string filename, string mimeType, Stream content);
Task DeleteAsync(Guid attachmentId);
```

The current implementation (`PostgresAttachmentStore` in `Organiz.Data`) can be replaced with any other backend (e.g. MinIO, local filesystem) by implementing this interface and changing the wiring in `App.axaml.cs`.

### UI

See [Attachment Bar](#attachment-bar) under NoteEditorControl.

---

## Kanban Boards

Any note can embed a link to a kanban board using the `kanban:UUID` link scheme. Boards are independent of the note tree — they are not notes and do not appear in the tree browser.

### Creating a Board

Click the **📋 Board** button in the `NoteEditorControl` toolbar (available when a note is loaded; disabled for the scratchpad):

1. An input dialog prompts for the board title.
2. A new board is created in the database.
3. A `[title](kanban:UUID)` link is inserted at the cursor position in the current note.
4. The **Kanban Window** opens immediately for the new board.

### Opening a Board

Clicking a `[text](kanban:UUID)` link in any note opens the **Kanban Window** for that board. If the window is already open, it is brought to the front rather than opening a duplicate.

### Kanban Window

The Kanban Window is a standalone, non-modal window.

| Area           | Description |
|----------------|-------------|
| Board title    | Editable TextBox at the top. Saved to the database on focus-out. |
| + Column       | Button in the top bar. Prompts for a title and appends a new column. |
| Hint bar       | One-line reminder of keyboard shortcuts. |
| Column area    | Horizontally scrollable list of column panels. |

#### Columns

Each column panel is 230 px wide and contains:

- An editable title TextBox (saved on focus-out).
- A scrollable list of card controls.
- A **× delete** button in the header — confirms before deleting (along with all its cards).
- A **+ Add Card** button at the bottom — prompts for a title via the input dialog.

#### Cards

Each card is a clickable `Border` control showing the card title. If the card is linked to a note, a 🔗 indicator is appended to the title.

- **Click** — selects the card (highlighted in blue).
- **Double-click** — opens the rename dialog.
- **Right-click** — context menu:

| Action | Behaviour |
|--------|-----------|
| **Rename** | Input dialog, pre-filled with the current title. |
| **Open Linked Note** | Opens the linked note in a Notes Form. *(Only shown when a note is linked.)* |
| **Unlink Note** | Clears the `note_id` reference. *(Only shown when a note is linked.)* |
| **Link to Note…** | Opens the **Note Picker Dialog** to search for and attach a note. *(Only shown when no note is linked.)* |
| **Delete Card** | Confirmation dialog, then permanent deletion. |

#### Keyboard Navigation

Keys are handled at the window level. They are ignored when a column title TextBox has keyboard focus.

| Key | Action |
|-----|--------|
| `↑` / `↓` | Move card selection up or down within the current column. |
| `←` / `→` | Move card selection to the adjacent column (matching position where possible). |
| `Shift+↑` / `Shift+↓` | Move the selected card up or down within its column. Persisted immediately. |
| `Shift+←` / `Shift+→` | Move the selected card to the adjacent column (appended at the end). Persisted immediately. |
| `F2` or `Enter` | Rename the selected card. |
| `Delete` | Delete the selected card (with confirmation). |

### Note Picker Dialog

Used when choosing a note to link to a kanban card. It is a modal dialog with:

- A search TextBox — press `Enter` to run the search.
- A results ListBox — displays note titles from a full-text search.
- **Link Note** button (enabled when a note is selected) and **Cancel**.

Returns the selected `Note` object to the caller; returns `null` if cancelled.

### Data Model

Boards, columns, and cards are stored in three dedicated tables (`kanban_boards`, `kanban_columns`, `kanban_cards`). Deleting a board cascades to its columns and cards. Cards reference notes via a nullable `note_id` FK with `ON DELETE SET NULL`, so cards survive note deletion (the 🔗 indicator simply disappears).

Sort order is maintained as an integer `sort_order` column. After any move operation, the affected column(s) are fully renumbered (`0, 1, 2, …`) in a small batch update.

### Swappable Interface

Board/column/card persistence is behind the `IKanbanRepository` interface in `Organiz.Core`, wired to `KanbanRepository` (`Organiz.Data`) in `App.axaml.cs`. An alternative backend can be substituted by implementing the interface.

---

## Configuration File

Organiz stores local configuration in a JSON file at:

```
~/.config/organiz/config.json
```

Created automatically on first launch if it does not exist.

### Schema

```json
{
  "lastProfile": "Personal",
  "profiles": [
    {
      "name": "Personal",
      "username": "alice",
      "database": {
        "host": "localhost",
        "port": 5432,
        "name": "organiz",
        "username": "postgres",
        "password": ""
      }
    },
    {
      "name": "Work",
      "username": "alice",
      "database": {
        "host": "work-server",
        "port": 5432,
        "name": "organiz",
        "username": "postgres",
        "password": ""
      }
    }
  ]
}
```

`lastProfile` records the name of the most recently used profile so it can be pre-selected in the picker on next launch.

### Profiles

Each entry in `profiles` bundles a display name, an Organiz username, and a full database connection config. This allows switching between entirely independent databases (e.g. personal, work, testing) without editing the file manually.

### Legacy Migration

Older installs stored `username` and `database` at the top level of `config.json`. On first load with the new format, `ConfigManager` detects this automatically, wraps the existing settings into a profile named **"Default"**, and re-saves the file in the new format. No manual migration is required.

---

## Profile Picker

The **Profile Picker** (`ProfilePickerWindow`) is a small modal that handles both startup profile selection and in-app profile management. It is the same window in both contexts; only the action button label differs.

| Mode | Trigger | Action button |
|------|---------|---------------|
| Startup | 0 or 2+ profiles exist at launch | **Open** |
| Manage | File → Switch Profile… | **Switch & Restart** |

### Behaviour

- The list shows all configured profiles. In manage mode, the currently active profile is marked with ✓.
- **Add** — opens `FirstRunWindow` as a modal dialog to enter a new profile name, username, and database settings. Duplicate profile names are rejected.
- **Edit** — opens `FirstRunWindow` pre-filled with the selected profile's current settings.
- **Delete** — confirmation dialog, then removes the profile. `lastProfile` is updated to the next available profile if the deleted one was active.
- **Open / Switch & Restart** — saves the selection to `lastProfile` in `config.json`, then:
  - In startup mode: proceeds with app initialisation.
  - In manage mode: saves all open notes, closes all note windows, restarts the process, and exits the current instance.

### First-ever launch (0 profiles)

The picker opens with an empty list and immediately triggers the Add dialog. If the user cancels without creating a profile, the app shuts down.

---

## Container Setup (Docker / Podman)

A `docker-compose.yml` (compatible with both `docker-compose` and `podman-compose`) is provided at the project root. It runs PostgreSQL 17 Alpine.

### Data Directory

PostgreSQL data is stored in a **bind mount** rather than a named volume, so you control exactly where data lives on the host. The mount source is configured via the `ORGANIZ_DATA_DIR` environment variable:

```yaml
volumes:
  - ${ORGANIZ_DATA_DIR:-./data}:/var/lib/postgresql/data
```

If `ORGANIZ_DATA_DIR` is not set, it defaults to `./data` relative to `docker-compose.yml`.

### Configuration

Copy `.env.example` to `.env` (gitignored) and set your preferred path:

```sh
cp .env.example .env
# then edit .env:
ORGANIZ_DATA_DIR=/home/alice/organiz-pgdata
```

`.env` is loaded automatically by both `docker-compose` and `podman-compose`. It is gitignored so personal paths are never committed. `.env.example` is committed and documents the available variables.

### Quick Start

```sh
cp .env.example .env          # configure data directory
podman-compose up -d          # (or: docker compose up -d)
```

---

## User Identification

Trust-based, no authentication.

1. Each **profile** carries its own username. On first launch (no profiles exist), the user is prompted to create a profile including a username.
2. No password required.
3. The username is stored in the active profile in `~/.config/organiz/config.json` and sent with all database operations to identify the user.
4. If the username does not exist in the `users` table, a new row is created automatically.
5. Different profiles can use different usernames, allowing a single install to act as different identities against different databases.

---

## First-Run & Database Initialization

On startup, Organiz checks the following in order:

1. **Profile selection** — Config is loaded from `~/.config/organiz/config.json`.
   - **No profiles** (first-ever launch): the **Profile Picker** opens and immediately prompts the user to add a profile (name, username, database connection). The app does not proceed until at least one profile exists.
   - **Exactly one profile**: it is used automatically — no picker is shown.
   - **Two or more profiles**: the **Profile Picker** is shown so the user can choose which database to open. The last-used profile is pre-selected.
2. **Database schema** — The application runs any pending schema migrations automatically against the selected profile's database. If the target database does not yet exist, the user is informed and startup is aborted with a clear error message (the app does not attempt to create the database itself; the PostgreSQL database must be created by the user or an install script).
3. **Root note** — If no root note exists for the current user (`is_root = TRUE AND created_by = userId`), one is created automatically with the title "Root".
4. **Scratchpad** — If no scratchpad row exists for the current user, one is created automatically (empty content).

---

## Project Structure

The solution is organized as a layered architecture:

```
Organiz.sln
├── Organiz.Core/          # Domain models, interfaces, business logic
├── Organiz.Data/          # PostgreSQL data access (repositories, migrations)
├── Organiz.UI/            # Avalonia application, forms, controls, ViewModels
└── Organiz.Tests/         # Unit and integration tests
```

| Project         | Responsibilities |
|-----------------|------------------|
| `Organiz.Core`  | Note, User, Scratchpad, AttachmentMeta, KanbanBoard/Column/Card models; repository/store interfaces; conflict resolution logic; export logic |
| `Organiz.Data`  | Npgsql-based repository implementations (including `KanbanRepository`); schema migrations (run on startup) |
| `Organiz.UI`    | Avalonia App, all Forms and Controls, ViewModels, config file management |
| `Organiz.Tests` | Tests for Core logic and Data layer |

---

## Note Deletion Behavior

When a parent note is **deleted**, its children become **orphans** (`parent_id` set to `NULL`).

### Single Source of Truth: In-App Links

`parent_id` is always derived from in-app links — it is never set directly from the UI. Every time a note is saved, the system (`SyncParentLinksAsync`) reconciles `parent_id` with the link graph:

- **Link added** (`[text](note:UUID)` appears in new content): if the referenced note is currently orphaned (`parent_id IS NULL`), its `parent_id` is set to the note being saved.
- **Link removed** or **child with no inbound links**: if no note in the database contains a link to a UUID, that note's `parent_id` is cleared (orphaned).

This means the Management Form's "Insert Link in Parent Note" action works by appending a real text link to the parent's content — the adoption happens as a side-effect of the normal save, not via a separate code path.

- Children are **not** cascade deleted.
- Orphaned notes appear in the **Management Form → Orphan Notes View**.
- The user can then re-link orphans to a new parent or delete them individually.

---

## Per-User Roots and Privacy

### Per-User Root Notes

Each user gets their own personal root note created automatically on first login. The root is identified by `is_root = TRUE AND created_by = userId`. The global single-root constraint is replaced by a per-user unique index.

### Note Privacy

Every note has:

| Field        | Description |
|--------------|-------------|
| `created_by` | The immutable creator of the note. Set at creation; never changes. |
| `is_private` | When `TRUE`, only `created_by` can view or open the note. Default is `FALSE` (public). |

#### Visibility Rules

| Who can see the note?       | Condition |
|-----------------------------|-----------|
| Everyone                    | `is_private = FALSE` |
| Creator only                | `is_private = TRUE` |

Visibility is enforced in:
- `GetChildrenAsync`: children hidden from others when private.
- `GetOrphansAsync`: private orphans hidden from other users.
- `SearchAsync`: private notes excluded from others' search results.
- `NotesWindow.OpenAsync`: hard block — opening another user's private note shows an error and returns without opening.

#### Toggling Privacy

A **Public** checkbox appears in the `NoteEditorControl` toolbar for all regular notes (hidden for the root note and scratchpad). The checkbox is disabled for notes the current user did not create (read-only view of others' public notes). Unchecking "Public" makes the note private on the next save.

#### Privacy Mismatch Warning

When using the Management Form's **Insert Link in Parent Note** to link a private orphan into a public parent note, a confirmation dialog warns the user that the link will make the private note reachable by anyone who can read the parent. The user must explicitly confirm before the link is inserted.

---

## Scratchpad Workflow

The scratchpad is a **user-managed workspace**. Moving content from the scratchpad into the note tree is done **manually by the user** (copy/paste). No dedicated "Move to..." automation is planned.
