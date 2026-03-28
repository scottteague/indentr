-- SQLite Migration 006: Soft-delete (Trash)

ALTER TABLE notes          ADD COLUMN deleted_at TEXT NULL DEFAULT NULL;
ALTER TABLE kanban_boards  ADD COLUMN deleted_at TEXT NULL DEFAULT NULL;
ALTER TABLE kanban_columns ADD COLUMN deleted_at TEXT NULL DEFAULT NULL;
ALTER TABLE kanban_cards   ADD COLUMN deleted_at TEXT NULL DEFAULT NULL;
ALTER TABLE attachments    ADD COLUMN deleted_at TEXT NULL DEFAULT NULL;

-- SQLite supports partial indexes with WHERE clauses
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
