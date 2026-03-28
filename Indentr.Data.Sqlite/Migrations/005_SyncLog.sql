-- SQLite Migration 005: Sync foundation
-- SQLite triggers use NEW/OLD row references directly; TG_TABLE_NAME and TG_OP
-- are PostgreSQL-specific. Each table needs its own INSERT/UPDATE/DELETE triggers.

CREATE TABLE IF NOT EXISTS sync_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT    NOT NULL,
    entity_id   TEXT    NOT NULL,
    operation   TEXT    NOT NULL CHECK (operation IN ('INSERT', 'UPDATE', 'DELETE')),
    occurred_at TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_sync_log_entity ON sync_log(entity_type, entity_id);

CREATE TABLE IF NOT EXISTS sync_state (
    id             INTEGER PRIMARY KEY DEFAULT 1,
    last_synced_at TEXT    NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z',
    CONSTRAINT sync_state_single_row CHECK (id = 1)
);

INSERT INTO sync_state (id, last_synced_at)
VALUES (1, '1970-01-01T00:00:00.0000000Z')
ON CONFLICT DO NOTHING;

-- ── notes triggers ────────────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_notes_sync_insert AFTER INSERT ON notes BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('notes', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_notes_sync_update AFTER UPDATE ON notes BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('notes', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_notes_sync_delete AFTER DELETE ON notes BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('notes', OLD.id, 'DELETE');
END;

-- ── scratchpads triggers ──────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_scratchpads_sync_insert AFTER INSERT ON scratchpads BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('scratchpads', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_scratchpads_sync_update AFTER UPDATE ON scratchpads BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('scratchpads', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_scratchpads_sync_delete AFTER DELETE ON scratchpads BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('scratchpads', OLD.id, 'DELETE');
END;

-- ── users triggers ────────────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_users_sync_insert AFTER INSERT ON users BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('users', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_users_sync_update AFTER UPDATE ON users BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('users', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_users_sync_delete AFTER DELETE ON users BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('users', OLD.id, 'DELETE');
END;

-- ── attachments triggers ──────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_attachments_sync_insert AFTER INSERT ON attachments BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('attachments', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_attachments_sync_update AFTER UPDATE ON attachments BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('attachments', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_attachments_sync_delete AFTER DELETE ON attachments BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('attachments', OLD.id, 'DELETE');
END;

-- ── kanban_boards triggers ────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_kanban_boards_sync_insert AFTER INSERT ON kanban_boards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_boards', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_boards_sync_update AFTER UPDATE ON kanban_boards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_boards', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_boards_sync_delete AFTER DELETE ON kanban_boards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_boards', OLD.id, 'DELETE');
END;

-- ── kanban_columns triggers ───────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_kanban_columns_sync_insert AFTER INSERT ON kanban_columns BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_columns', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_columns_sync_update AFTER UPDATE ON kanban_columns BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_columns', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_columns_sync_delete AFTER DELETE ON kanban_columns BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_columns', OLD.id, 'DELETE');
END;

-- ── kanban_cards triggers ─────────────────────────────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_kanban_cards_sync_insert AFTER INSERT ON kanban_cards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_cards', NEW.id, 'INSERT');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_cards_sync_update AFTER UPDATE ON kanban_cards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_cards', NEW.id, 'UPDATE');
END;
CREATE TRIGGER IF NOT EXISTS trg_kanban_cards_sync_delete AFTER DELETE ON kanban_cards BEGIN
    INSERT INTO sync_log(entity_type, entity_id, operation) VALUES ('kanban_cards', OLD.id, 'DELETE');
END;

INSERT INTO schema_migrations(version) VALUES (5) ON CONFLICT DO NOTHING;
