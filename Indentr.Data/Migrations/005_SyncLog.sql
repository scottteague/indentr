-- Migration 005: Sync foundation
--
-- Adds the infrastructure needed for local→remote PostgreSQL sync:
--   • sync_log   — one row per INSERT/UPDATE/DELETE on any entity table,
--                  populated automatically by triggers. Consumed by SyncService
--                  to push local changes to the remote database.
--   • sync_state — single-row table tracking the last successful sync time,
--                  used to filter "what has the remote changed since last sync".
--   • updated_at — added to kanban_columns and kanban_cards so the pull side
--                  can filter by timestamp just like notes and scratchpads.
--
-- The triggers fire on the local DB only. Entries are cleaned up by SyncService
-- after they have been confirmed on the remote end.

-- ── updated_at on kanban sub-tables ──────────────────────────────────────────

ALTER TABLE kanban_columns
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

ALTER TABLE kanban_cards
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

-- ── sync_log ─────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS sync_log (
    id           BIGSERIAL    PRIMARY KEY,
    entity_type  TEXT         NOT NULL,  -- table name: notes | scratchpads | users | attachments
                                         --             kanban_boards | kanban_columns | kanban_cards
    entity_id    UUID         NOT NULL,
    operation    TEXT         NOT NULL   CHECK (operation IN ('INSERT', 'UPDATE', 'DELETE')),
    occurred_at  TIMESTAMPTZ  NOT NULL   DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sync_log_occurred_at ON sync_log(occurred_at);
CREATE INDEX IF NOT EXISTS idx_sync_log_entity      ON sync_log(entity_type, entity_id);

-- ── sync_state ───────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS sync_state (
    id             INTEGER      PRIMARY KEY DEFAULT 1,
    last_synced_at TIMESTAMPTZ  NOT NULL DEFAULT '1970-01-01T00:00:00Z',
    CONSTRAINT sync_state_single_row CHECK (id = 1)
);

INSERT INTO sync_state (id, last_synced_at)
VALUES (1, '1970-01-01T00:00:00Z')
ON CONFLICT DO NOTHING;

-- ── Shared trigger functions ──────────────────────────────────────────────────

-- fn_sync_log: appends one row to sync_log for every row-level change.
-- Uses TG_TABLE_NAME as entity_type so one function covers all tables.
CREATE OR REPLACE FUNCTION fn_sync_log()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        INSERT INTO sync_log (entity_type, entity_id, operation)
        VALUES (TG_TABLE_NAME, OLD.id, 'DELETE');
        RETURN OLD;
    ELSE
        INSERT INTO sync_log (entity_type, entity_id, operation)
        VALUES (TG_TABLE_NAME, NEW.id, TG_OP);
        RETURN NEW;
    END IF;
END;
$$;

-- fn_set_updated_at: stamps updated_at = NOW() on every UPDATE.
-- Applied to kanban_columns and kanban_cards which lacked this column.
CREATE OR REPLACE FUNCTION fn_set_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

-- ── Sync-log triggers (all entity tables) ────────────────────────────────────

CREATE OR REPLACE TRIGGER trg_notes_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON notes
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

CREATE OR REPLACE TRIGGER trg_scratchpads_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON scratchpads
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

CREATE OR REPLACE TRIGGER trg_users_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON users
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

-- NOTE: trg_attachment_lo_cleanup (BEFORE DELETE) already exists on attachments.
-- This AFTER trigger is independent and fires after the LO is already unlinked.
CREATE OR REPLACE TRIGGER trg_attachments_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON attachments
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

CREATE OR REPLACE TRIGGER trg_kanban_boards_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON kanban_boards
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

CREATE OR REPLACE TRIGGER trg_kanban_columns_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON kanban_columns
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

CREATE OR REPLACE TRIGGER trg_kanban_cards_sync_log
    AFTER INSERT OR UPDATE OR DELETE ON kanban_cards
    FOR EACH ROW EXECUTE FUNCTION fn_sync_log();

-- ── updated_at maintenance triggers ─────────────────────────────────────────

CREATE OR REPLACE TRIGGER trg_kanban_columns_updated_at
    BEFORE UPDATE ON kanban_columns
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

CREATE OR REPLACE TRIGGER trg_kanban_cards_updated_at
    BEFORE UPDATE ON kanban_cards
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

INSERT INTO schema_migrations(version) VALUES (5) ON CONFLICT DO NOTHING;
