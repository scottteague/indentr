-- Migration 004: Kanban boards
-- Three tables: boards → columns → cards.
-- Deleting a board cascades to columns and cards.
-- Cards may optionally reference a note; note deletion NULLs the reference.

CREATE TABLE IF NOT EXISTS kanban_boards (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    title      TEXT        NOT NULL DEFAULT '',
    owner_id   UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS kanban_columns (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    board_id   UUID NOT NULL REFERENCES kanban_boards(id) ON DELETE CASCADE,
    title      TEXT NOT NULL DEFAULT '',
    sort_order INT  NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS kanban_cards (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    column_id  UUID        NOT NULL REFERENCES kanban_columns(id) ON DELETE CASCADE,
    title      TEXT        NOT NULL DEFAULT '',
    note_id    UUID        REFERENCES notes(id) ON DELETE SET NULL,
    sort_order INT         NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_kanban_columns_board_id ON kanban_columns(board_id);
CREATE INDEX IF NOT EXISTS idx_kanban_cards_column_id  ON kanban_cards(column_id);

INSERT INTO schema_migrations(version) VALUES (4) ON CONFLICT DO NOTHING;
