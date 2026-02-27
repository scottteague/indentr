# Web Parity TODO

Features present in the desktop UI that are missing or incomplete in the web project.

---

## Note Editing

- [ ] **Editable title** — the title is currently a read-only `<h1>`. Add an editable field and save on blur/enter, matching the desktop `TitleBox` behaviour including propagating link-title updates to other notes.

- [ ] **Formatting toolbar** — Bold, Red, Italic, Underline buttons that wrap the CodeMirror selection in the appropriate Markdown syntax. The desktop `NoteEditorControl` has these as simple wrap-selection operations.

- [ ] **Link insertion dialog** — a UI to insert `[text](note:UUID)` or `[text](kanban:UUID)` links without knowing the ID. On desktop this is the `LinkTargetDialog` with a note-picker. Could be a search-as-you-type modal.

- [ ] **New child note** — a "+ Note" action that creates a child note from the current selection and inserts an in-app link at the cursor, matching `OnNewChildNoteClick` on the desktop.

- [ ] **Privacy toggle** — a Public/Private checkbox per note (hidden for root), saved on the next save. The web currently ignores `is_private` except to block access.

- [ ] **Note deletion** — a way to soft-delete the current note from the note editor (e.g. a "Move to Trash" menu item or button). The desktop has this in the Notes Form `Note` menu.

---

## Kanban Boards

- [ ] **Kanban board page** — a `/board/{id}` route with a full board view: horizontally scrollable column list, card list per column, editable column and card titles. The desktop `KanbanWindow` is the reference.

- [ ] **Kanban board creation** — a "+ Board" action in the note editor that creates a board and inserts a `kanban:UUID` link, matching `OnNewBoardClick`.

- [ ] **Clicking kanban links** — `OnLinkClicked` in `NoteEditor.razor` currently ignores `kanban:` targets. It should navigate to `/board/{id}`.

- [ ] **Card actions** — rename, delete, link to an existing note, create and link a new note (right-click context menu equivalents on desktop).

- [ ] **Card keyboard navigation** — arrow keys to move selection, Shift+arrow to reorder cards and move between columns.

---

## Attachments

- [ ] **Attachment upload** — a file picker to attach files to the current note. The desktop `OnAttachClick` stores the file as a PostgreSQL large object. Needs a multipart upload endpoint or direct Blazor streaming.

- [ ] **Attachment delete** — remove an attachment from a note (with confirmation). Currently the web only lists attachments as download links.

---

## Trash

- [ ] **Trash page** (`/trash`) — lists all soft-deleted notes (and eventually kanban items), sorted by deletion time. Matches the desktop `TrashWindow`.

- [ ] **Restore** — un-delete a trashed note (clears `deleted_at`).

- [ ] **Permanent delete** — hard DELETE with confirmation dialog.

- [ ] **Empty Trash** — bulk permanent-delete everything in trash.

---

## Management / Orphans

- [ ] **Orphan notes page** (`/manage` or tab on a management page) — lists notes with no parent. Matches the Orphan Notes View in the desktop Management Form.

- [ ] **Tree browser** — a full browsable tree to pick a parent note when linking an orphan. The desktop Management Form's Tree Browser View is the reference.

- [ ] **Insert link in parent** — appends an in-app link to a chosen parent note, triggering the normal `SyncParentLinksAsync` adoption path.

---

## Export / Import

- [ ] **Single-note export** — download the current note as a `.md` file, with in-app links stripped to plain text.

- [ ] **Subtree export** — trigger `SubtreeExporter.ExportAsync` and serve the result as a `.zip` download (since the user can't pick a local folder from a browser).

- [ ] **Subtree import** — upload a `.zip` export and call `SubtreeImporter.ImportAsync`.

---

## Sync

- [ ] **Sync status bar** — `SyncStatusBar.razor` is currently empty. Wire up `SyncService` and show last-synced time, offline/error state, and a manual sync button, when a remote DB is configured in the profile.

---

## Profile Management

- [ ] **In-app profile switching** — the web can auto-select from `LastProfile` but there is no UI to add, edit, or delete profiles, or to switch to a different one without editing `config.json` by hand.
