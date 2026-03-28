-- SQLite Migration 003: Attachments
-- Bytes stored directly as BLOB (no PostgreSQL large objects).
-- The lo_unlink trigger from the PostgreSQL version is not needed;
-- deleting a row is sufficient since the bytes live in the row itself.

CREATE TABLE IF NOT EXISTS attachments (
    id         TEXT PRIMARY KEY,
    note_id    TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    data       BLOB NOT NULL,
    filename   TEXT NOT NULL,
    mime_type  TEXT NOT NULL DEFAULT 'application/octet-stream',
    size       INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_attachments_note_id ON attachments(note_id);

INSERT INTO schema_migrations(version) VALUES (3) ON CONFLICT DO NOTHING;
