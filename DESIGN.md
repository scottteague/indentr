# Organiz - Design Document

## Overview

Organiz is a note-taking application inspired by [Tomboy Notes](https://wiki.gnome.org/Apps/Tomboy). Notes are organized as an **N-Tree** data structure where any note can have unlimited children. The app is built for **multi-user** use with a trust-based identity model and conflict-safe persistence.

## Technology Stack

| Component   | Technology                        |
|-------------|-----------------------------------|
| Language    | C# / .NET 10                      |
| UI          | Avalonia (latest .NET 10 compatible) |
| Database    | PostgreSQL                        |
| Auth Model  | Trust-based (no login, user self-identifies) |

---

## Architecture

### Data Structure: N-Tree

All notes exist in a single N-Tree. Each note can have **unlimited children**. The tree has a single **root node** which is displayed on the Main Form at startup.

```
Root Note
├── Child Note A
│   ├── Grandchild A1
│   └── Grandchild A2
│       └── Great-Grandchild A2a
├── Child Note B
└── Child Note C
```

**Orphan notes** are notes that have no parent in the tree. These can arise from deletion of a parent or from conflict resolution. Orphans are managed via the Management Form.

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
| Scratchpad  | A second instance of the **NoteEditorControl** for temporary content. Per-user, persisted to DB. Content may later be moved into the tree. |

### 2. Notes Form

Opened when a user clicks an **in-app link** within any note. Displays a single note using the **NoteEditorControl**.

| Area        | Description |
|-------------|-------------|
| Note Area   | An instance of the **NoteEditorControl** showing the linked note. |

Each in-app link click opens a **new Notes Form window**.

### 3. Management Form

Used to manage **orphan notes** and browse the full tree. Has two views:

| View               | Description |
|--------------------|-------------|
| Orphan Notes View  | Lists all notes with no parent. User can select an orphan to delete or link. |
| Tree Browser View  | Displays the full note tree. Used to select a **target parent** when linking an orphan. |

**Workflow for linking an orphan:**
1. User selects an orphan from the Orphan Notes View.
2. User browses the Tree Browser View and selects a parent note.
3. The orphan becomes a child of the selected parent.

**Deletion** of notes is also available from this form.

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
| External Links| `[link text](https://...)` — standard URL. |

### Toolbar Buttons

Each button wraps/unwraps the selected text with Markdown syntax. Formatting is **combinable**.

| Button        | Markdown Applied          | Visual Rendering |
|---------------|---------------------------|------------------|
| **Bold**      | `**selected text**`       | Bold text |
| **Red**       | `__selected text__`       | Red-colored text |
| **Underline** | `<u>selected text</u>`   | Underlined text |
| **Link**      | `[selected text](target)` | Clickable link (prompts for target) |

**Bold + Red example:** `**__text__**` — renders as bold and red simultaneously.

### Rendering Rules

The editor applies custom rendering on top of standard Markdown:

| Markdown Syntax       | Rendered As |
|-----------------------|-------------|
| `**text**` (asterisks)| **Bold** text |
| `__text__` (underscores)| **Red-colored** text (not bold) |
| `<u>text</u>`        | Underlined text |
| `[text](note:UUID)`  | Clickable in-app link |
| `[text](http...)`    | Clickable external link |

> **Key deviation from standard Markdown:** `__text__` is rendered as red, not bold. This is an intentional Organiz-specific rendering rule.

### Link Behavior

| Condition                          | Action |
|------------------------------------|--------|
| Link target starts with `http`     | Opens in the system's default external browser. |
| Link target starts with `note:`    | Opens a new **Notes Form** displaying the linked note (by UUID). |

---

## Search

- **Full-text search** across all note content.
- Implementation should leverage PostgreSQL full-text search capabilities (`tsvector` / `tsquery`).

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
| `is_root`      | `BOOLEAN`       | TRUE for the single root note. |
| `title`        | `TEXT`          | Note title. |
| `content`      | `TEXT`          | Raw Markdown source. |
| `content_hash` | `TEXT`          | Hash of `content` for conflict detection. |
| `owner_id`     | `UUID` FK       | References `users.id`. The user who last edited. |
| `sort_order`   | `INTEGER`       | Ordering among siblings. |
| `created_at`   | `TIMESTAMPTZ`   | Row creation time. |
| `updated_at`   | `TIMESTAMPTZ`   | Last modification time. |
| `search_vector`| `TSVECTOR`      | Generated column for full-text search. |

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
- `notes.is_root` — partial unique index (`WHERE is_root = TRUE`) to enforce single root.

### Content Format (Raw Markdown)

The `content` field stores raw Markdown text. Example:

```markdown
This is a note with **bold text** and __red text__ and <u>underlined text</u>.

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

## User Identification

Trust-based, no authentication.

1. On first launch, the user is prompted to **type a username**.
2. No password required.
3. The username is stored locally and sent with all database operations to identify the user.
4. If the username does not exist in the `users` table, a new row is created automatically.

---

## Note Deletion Behavior

When a parent note is **deleted**, its children become **orphans** (`parent_id` set to `NULL`).

- Children are **not** cascade deleted.
- Orphaned notes appear in the **Management Form → Orphan Notes View**.
- The user can then re-link orphans to a new parent or delete them individually.

---

## Scratchpad Workflow

The scratchpad is a **user-managed workspace**. Moving content from the scratchpad into the note tree is done **manually by the user** (copy/paste). No dedicated "Move to..." automation is planned.
