-- SQLite Migration 002: Per-user root notes and note privacy
-- SQLite does not support ALTER TABLE ... ALTER COLUMN ... SET NOT NULL;
-- the application always sets created_by before inserting.

ALTER TABLE notes ADD COLUMN created_by TEXT;
UPDATE notes SET created_by = owner_id WHERE created_by IS NULL;

ALTER TABLE notes ADD COLUMN is_private INTEGER NOT NULL DEFAULT 0;

-- One root per user (partial index using WHERE)
CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_root_per_user ON notes(created_by) WHERE is_root = 1;

INSERT INTO schema_migrations(version) VALUES (2) ON CONFLICT DO NOTHING;
