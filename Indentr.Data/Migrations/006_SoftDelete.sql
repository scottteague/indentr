-- Migration 006: Soft-delete (Trash)
--
-- Adds a deleted_at column to all entity tables so rows can be moved to a
-- "Trash" state rather than being permanently deleted.  A non-null deleted_at
-- marks the row as trashed; NULL means active.
--
-- Permanent deletion still uses a hard DELETE (which triggers LO cleanup for
-- attachments and cascades for kanban children at the DB level).
-- Soft-deleted rows propagate to remote via the existing sync_log UPDATE path.

ALTER TABLE notes          ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ NULL DEFAULT NULL;
ALTER TABLE kanban_boards  ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ NULL DEFAULT NULL;
ALTER TABLE kanban_columns ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ NULL DEFAULT NULL;
ALTER TABLE kanban_cards   ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ NULL DEFAULT NULL;
ALTER TABLE attachments    ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ NULL DEFAULT NULL;

CREATE INDEX IF NOT EXISTS idx_notes_deleted
    ON notes(created_by, deleted_at) WHERE deleted_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_boards_deleted
    ON kanban_boards(owner_id, deleted_at) WHERE deleted_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_columns_deleted
    ON kanban_columns(board_id, deleted_at) WHERE deleted_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_cards_deleted
    ON kanban_cards(column_id, deleted_at) WHERE deleted_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_attachments_deleted
    ON attachments(note_id, deleted_at) WHERE deleted_at IS NOT NULL;

INSERT INTO schema_migrations(version) VALUES (6) ON CONFLICT DO NOTHING;
