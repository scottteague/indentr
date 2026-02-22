-- Migration 003: Attachments
-- File content is stored as PostgreSQL Large Objects; this table holds the metadata
-- and the OID reference into pg_largeobject.

CREATE TABLE IF NOT EXISTS attachments (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    note_id    UUID        NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    lo_oid     OID         NOT NULL,
    filename   TEXT        NOT NULL,
    mime_type  TEXT        NOT NULL DEFAULT 'application/octet-stream',
    size       BIGINT      NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_attachments_note_id ON attachments(note_id);

-- Automatically unlink the large object whenever an attachment row is deleted,
-- whether by an explicit DELETE or by ON DELETE CASCADE from the parent note.
CREATE OR REPLACE FUNCTION fn_unlink_attachment_lo()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    PERFORM lo_unlink(OLD.lo_oid);
    RETURN OLD;
END;
$$;

CREATE OR REPLACE TRIGGER trg_attachment_lo_cleanup
    BEFORE DELETE ON attachments
    FOR EACH ROW EXECUTE FUNCTION fn_unlink_attachment_lo();

INSERT INTO schema_migrations(version) VALUES (3) ON CONFLICT DO NOTHING;
