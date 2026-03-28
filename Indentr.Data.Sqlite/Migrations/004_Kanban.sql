-- SQLite Migration 004: Kanban boards

CREATE TABLE IF NOT EXISTS kanban_boards (
    id         TEXT    PRIMARY KEY,
    title      TEXT    NOT NULL DEFAULT '',
    owner_id   TEXT    NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS kanban_columns (
    id         TEXT    PRIMARY KEY,
    board_id   TEXT    NOT NULL REFERENCES kanban_boards(id) ON DELETE CASCADE,
    title      TEXT    NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS kanban_cards (
    id         TEXT    PRIMARY KEY,
    column_id  TEXT    NOT NULL REFERENCES kanban_columns(id) ON DELETE CASCADE,
    title      TEXT    NOT NULL DEFAULT '',
    note_id    TEXT    REFERENCES notes(id) ON DELETE SET NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT    NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_kanban_columns_board_id ON kanban_columns(board_id);
CREATE INDEX IF NOT EXISTS idx_kanban_cards_column_id  ON kanban_cards(column_id);

INSERT INTO schema_migrations(version) VALUES (4) ON CONFLICT DO NOTHING;
