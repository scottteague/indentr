-- SQLite Migration 001: Initial schema
-- Types: UUIDs as TEXT, booleans as INTEGER (0/1), timestamps as TEXT (ISO 8601 UTC)

CREATE TABLE IF NOT EXISTS schema_migrations (
    version    INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS users (
    id         TEXT PRIMARY KEY,
    username   TEXT UNIQUE NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS notes (
    id           TEXT PRIMARY KEY,
    parent_id    TEXT REFERENCES notes(id) ON DELETE SET NULL,
    is_root      INTEGER NOT NULL DEFAULT 0,
    title        TEXT NOT NULL DEFAULT '',
    content      TEXT NOT NULL DEFAULT '',
    content_hash TEXT NOT NULL DEFAULT '',
    owner_id     TEXT NOT NULL REFERENCES users(id),
    sort_order   INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
    -- No search_vector: SQLite uses LIKE search in SearchAsync
);

CREATE TABLE IF NOT EXISTS scratchpads (
    id           TEXT PRIMARY KEY,
    user_id      TEXT UNIQUE NOT NULL REFERENCES users(id),
    content      TEXT NOT NULL DEFAULT '',
    content_hash TEXT NOT NULL DEFAULT '',
    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_notes_parent_id ON notes(parent_id);

INSERT INTO schema_migrations(version) VALUES (1) ON CONFLICT DO NOTHING;
