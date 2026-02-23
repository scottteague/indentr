# Indentr

A multi-user, tree-structured note-taking app for the desktop, backed by PostgreSQL. Inspired by [Tomboy Notes](https://wiki.gnome.org/Apps/Tomboy).

> Notes link to notes. Notes link to boards. Everything is just Markdown.

---

## Features

- **Linked notes** — write `[My Note](note:UUID)` to link notes together. Clicking the link opens the note in a new window.
- **N-Tree structure** — every note can have unlimited children. The tree grows as you link notes together.
- **Kanban boards** — create a board from any note with the 📋 button. Cards on the board can link back to notes, or create new ones on the fly with a double-click.
- **Attachments** — attach files to any note. Stored in PostgreSQL as large objects; open or save them from a bar at the bottom of the editor.
- **Full-text search** — PostgreSQL `tsvector`-powered search across all notes.
- **Scratchpad** — a per-user scratch space that isn't part of the note tree.
- **Multi-user** — multiple people can share one database. Notes can be marked public or private.
- **Multiple profiles** — keep separate databases (personal, work, etc.) and switch between them from the menu.
- **Optional remote sync** — pair any profile with a remote PostgreSQL server. Indentr pushes your changes and pulls others' on a 10-minute timer (or on demand with `Shift+Ctrl+S`). Works offline; the remote is best-effort only.
- **Conflict-safe saves** — optimistic concurrency via content hashing. Conflicting edits are saved as a sibling `[CONFLICT]` note rather than silently overwritten.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL 14 or later (PostgreSQL 17 recommended)
- Linux, macOS, or Windows

A `docker-compose.yml` / `podman-compose.yml` is included if you want to spin up a local Postgres quickly.

---

## Quick Start

### 1. Start PostgreSQL

```sh
cp .env.example .env        # set INDENTR_DATA_DIR to where you want pg data stored
podman-compose up -d        # or: docker compose up -d
```

Then create the database (one-time):

```sh
podman exec -it indentr-db psql -U postgres -c "CREATE DATABASE indentr;"
```

### 2. Build and run

```sh
dotnet run --project Indentr.UI
```

On first launch you'll be prompted to create a **profile** — give it a name, pick a username, and fill in the database connection details. Indentr creates the schema automatically.

---

## Usage

### Notes

The main window shows your **root note** — think of it as your personal home page. Write anything in Markdown. Use the toolbar for formatting, or type Markdown directly.

**To create a child note:** select some text and click **+ Note**. The selected text becomes the new note's title, and a link is inserted in place of the selection.

**To navigate:** click any `[link](note:…)` in the editor. The current note is saved first, then the linked note opens in a new window.

**Keyboard shortcut:** `Ctrl+S` saves. `Ctrl+Q` closes a note window. `Shift+Ctrl+Q` closes all note windows.

### Kanban Boards

Click **📋 Board** in the toolbar to create a board. A `[Board Name](kanban:…)` link is inserted in your note and the board window opens.

- **Add columns** with the **+ Column** button.
- **Add cards** with the **+ Add Card** button at the bottom of each column.
- **Double-click a card** to open its linked note (or create one automatically if it doesn't have one yet).
- **Rename a card** with `F2` or right-click → Rename.
- **Move cards** with `Shift+↑↓` (within a column) or `Shift+←→` (between columns).

### Attachments

The bottom bar of any note editor has a **📎 Attach** button. Attached files are stored in the database. Click a chip to open the file, or right-click for Save As / Delete.

### Search

**File → Search…** opens a full-text search window. Click any result to open that note.

### Profiles

**File → Switch Profile…** lets you add, edit, delete, or switch between database profiles. Switching saves all open notes and restarts the app.

### Remote Sync

Each profile can optionally sync to a remote PostgreSQL database, which lets multiple machines (or users) share notes.

**To enable sync for a profile:**

1. Open **File → Switch Profile…**.
2. Select the profile and click **Edit**.
3. In the profile editor, fill in the **Remote Database** section (host, port, database name, username, password).
4. Use the **Test Connection** button to verify the remote is reachable.
5. Click **Save**.

Once configured, Indentr syncs automatically every 10 minutes. You can also trigger an immediate sync with **`Shift+Ctrl+S`** (saves all open windows first, then syncs).

**Status bar** — the bottom of the main window shows the sync state:

| Status | Meaning |
|--------|---------|
| `Synced at 14:32` | Last sync completed successfully at that time. |
| `Offline` | Remote was unreachable; will retry on the next timer tick. |
| `Sync failed: …` | An error occurred; the message gives a short reason. |

The status bar is hidden entirely when no remote database is configured for the active profile.

**Profile picker** — when you open the profile picker, each profile that has a remote configured shows a dimmed sub-line with its last sync time (e.g. `Synced today at 14:32` or `Never synced`).

**Privacy during sync** — only notes you created, or public notes, are synced to other users' local databases. Private notes written by others are never pulled to your machine.

---

## Markdown rendering

Indentr renders a subset of Markdown live in the editor:

| Syntax | Renders as |
|--------|-----------|
| `**text**` | **Bold** |
| `__text__` | Red text *(Indentr-specific)* |
| `*text*` | *Italic* |
| `_text_` | Underline *(Indentr-specific)* |
| `` `code` `` | Inline code (monospace, shaded) |
| ` ```block``` ` | Code block (monospace, shaded) |
| `# Heading` … `###### Heading` | H1–H6 with scaled size |
| `- [ ] item` | Unchecked task (red) |
| `- [x] item` | Checked task (green) |
| `[text](note:UUID)` | In-app note link (blue) |
| `[text](kanban:UUID)` | Kanban board link (purple) |
| `[text](https://…)` | External link (opens browser) |

---

## Project structure

```
Indentr.sln
├── Indentr.Core/    # Models, interfaces, business logic
├── Indentr.Data/    # PostgreSQL repositories, schema migrations
├── Indentr.UI/      # Avalonia UI — windows, controls, config
└── Indentr.Tests/   # Tests
```

Built with [Avalonia](https://avaloniaui.net/) and [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit). Database access via [Npgsql](https://www.npgsql.org/) (raw ADO.NET, no ORM).

For a full technical reference see [DESIGN.md](DESIGN.md).

---

## License

GPL v3 — see [LICENSE](LICENSE) for the full text.

Copyright (C) 2024 the Indentr contributors.
