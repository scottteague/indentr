-- Migration 002: Per-user root notes and note privacy

-- created_by is the immutable creator of the note (owner_id tracks who last edited).
ALTER TABLE notes ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id);

-- Seed created_by from owner_id for all existing rows.
UPDATE notes SET created_by = owner_id WHERE created_by IS NULL;

-- Now enforce NOT NULL.
ALTER TABLE notes ALTER COLUMN created_by SET NOT NULL;

-- is_private: when TRUE, only the creator can see / open the note.
-- Default FALSE so all existing notes remain public.
ALTER TABLE notes ADD COLUMN IF NOT EXISTS is_private BOOLEAN NOT NULL DEFAULT FALSE;

-- Drop the global single-root constraint; each user gets their own root.
DROP INDEX IF EXISTS idx_notes_single_root;
CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_root_per_user ON notes(created_by) WHERE is_root = TRUE;

INSERT INTO schema_migrations(version) VALUES (2) ON CONFLICT DO NOTHING;
