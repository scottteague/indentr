-- Schema migrations tracking
CREATE TABLE IF NOT EXISTS schema_migrations (
    version     INTEGER PRIMARY KEY,
    applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Users
CREATE TABLE IF NOT EXISTS users (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username    TEXT UNIQUE NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Notes
CREATE TABLE IF NOT EXISTS notes (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_id       UUID REFERENCES notes(id) ON DELETE SET NULL,
    is_root         BOOLEAN NOT NULL DEFAULT FALSE,
    title           TEXT NOT NULL DEFAULT '',
    content         TEXT NOT NULL DEFAULT '',
    content_hash    TEXT NOT NULL DEFAULT '',
    owner_id        UUID NOT NULL REFERENCES users(id),
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    search_vector   TSVECTOR GENERATED ALWAYS AS
                        (to_tsvector('english', coalesce(title,'') || ' ' || coalesce(content,''))) STORED
);

-- Scratchpads (one per user)
CREATE TABLE IF NOT EXISTS scratchpads (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID UNIQUE NOT NULL REFERENCES users(id),
    content         TEXT NOT NULL DEFAULT '',
    content_hash    TEXT NOT NULL DEFAULT '',
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX  IF NOT EXISTS idx_notes_parent_id     ON notes(parent_id);
CREATE INDEX  IF NOT EXISTS idx_notes_search_vector ON notes USING GIN(search_vector);
CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_single_root ON notes(is_root) WHERE is_root = TRUE;

INSERT INTO schema_migrations(version) VALUES (1) ON CONFLICT DO NOTHING;
