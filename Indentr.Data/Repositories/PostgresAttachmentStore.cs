using Npgsql;
using NpgsqlTypes;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Repositories;

public class PostgresAttachmentStore(string connectionString) : IAttachmentStore
{
    public async Task<IReadOnlyList<AttachmentMeta>> ListForNoteAsync(Guid noteId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, note_id, filename, mime_type, size, created_at " +
            "FROM attachments WHERE note_id = @noteId ORDER BY created_at",
            conn);
        cmd.Parameters.AddWithValue("noteId", noteId);
        var results = new List<AttachmentMeta>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            results.Add(MapMeta(r));
        return results;
    }

    public async Task<(AttachmentMeta Meta, Stream Content)?> OpenReadAsync(Guid attachmentId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // lo_get() requires an active transaction.
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT id, note_id, filename, mime_type, size, created_at, lo_get(lo_oid) " +
            "FROM attachments WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var meta  = MapMeta(r);
        var bytes = r.GetFieldValue<byte[]>(6);
        await r.CloseAsync();

        await tx.CommitAsync();
        return (meta, new MemoryStream(bytes));
    }

    public async Task<AttachmentMeta> StoreAsync(Guid noteId, string filename, string mimeType, Stream content)
    {
        // Read the full content before opening the connection so the
        // DB transaction is held for as short a time as possible.
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms);
        var bytes = ms.ToArray();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // lo_from_bytea() requires an active transaction.
        await using var tx = await conn.BeginTransactionAsync();

        // Create the large object from the raw bytes; returns its OID.
        await using var loCmd = new NpgsqlCommand("SELECT lo_from_bytea(0, @data)", conn);
        loCmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = bytes });
        await using var loReader = await loCmd.ExecuteReaderAsync();
        await loReader.ReadAsync();
        var oid = loReader.GetFieldValue<uint>(0);
        await loReader.CloseAsync();

        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO attachments (id, note_id, lo_oid, filename, mime_type, size)
              VALUES (@id, @noteId, @oid, @filename, @mimeType, @size)
              RETURNING created_at",
            conn);
        cmd.Parameters.AddWithValue("id",       id);
        cmd.Parameters.AddWithValue("noteId",   noteId);
        cmd.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = oid });
        cmd.Parameters.AddWithValue("filename", filename);
        cmd.Parameters.AddWithValue("mimeType", mimeType);
        cmd.Parameters.AddWithValue("size",     (long)bytes.Length);

        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        var createdAt = r.GetDateTime(0);
        await r.CloseAsync();

        await tx.CommitAsync();
        return new AttachmentMeta
        {
            Id        = id,
            NoteId    = noteId,
            Filename  = filename,
            MimeType  = mimeType,
            Size      = bytes.Length,
            CreatedAt = createdAt
        };
    }

    public async Task DeleteAsync(Guid attachmentId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // trg_attachment_lo_cleanup calls lo_unlink() automatically on delete.
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM attachments WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static AttachmentMeta MapMeta(NpgsqlDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        NoteId    = r.GetGuid(1),
        Filename  = r.GetString(2),
        MimeType  = r.GetString(3),
        Size      = r.GetInt64(4),
        CreatedAt = r.GetDateTime(5)
    };
}
