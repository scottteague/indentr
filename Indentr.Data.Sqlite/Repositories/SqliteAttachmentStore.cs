using Microsoft.Data.Sqlite;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Sqlite.Repositories;

public class SqliteAttachmentStore(string dbPath) : IAttachmentStore
{
    public async Task<IReadOnlyList<AttachmentMeta>> ListForNoteAsync(Guid noteId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, note_id, filename, mime_type, size, created_at, deleted_at " +
            "FROM attachments WHERE note_id = @noteId AND deleted_at IS NULL ORDER BY created_at",
            conn);
        cmd.Parameters.AddWithValue("@noteId", noteId.ToString());
        var results = new List<AttachmentMeta>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            results.Add(MapMeta(r));
        return results;
    }

    public async Task<(AttachmentMeta Meta, Stream Content)?> OpenReadAsync(Guid attachmentId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, note_id, filename, mime_type, size, created_at, deleted_at, data " +
            "FROM attachments WHERE id = @id AND deleted_at IS NULL",
            conn);
        cmd.Parameters.AddWithValue("@id", attachmentId.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var meta  = MapMeta(r);
        var bytes = r.GetFieldValue<byte[]>(7);
        return (meta, new MemoryStream(bytes));
    }

    public async Task<AttachmentMeta> StoreAsync(Guid noteId, string filename, string mimeType, Stream content)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms);
        var bytes = ms.ToArray();

        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var cmd = new SqliteCommand(
            "INSERT INTO attachments (id, note_id, data, filename, mime_type, size, created_at) " +
            "VALUES (@id, @noteId, @data, @filename, @mimeType, @size, @now)",
            conn);
        cmd.Parameters.AddWithValue("@id",       id);
        cmd.Parameters.AddWithValue("@noteId",   noteId.ToString());
        cmd.Parameters.AddWithValue("@data",     bytes);
        cmd.Parameters.AddWithValue("@filename", filename);
        cmd.Parameters.AddWithValue("@mimeType", mimeType);
        cmd.Parameters.AddWithValue("@size",     (long)bytes.Length);
        cmd.Parameters.AddWithValue("@now",      now);
        await cmd.ExecuteNonQueryAsync();

        return new AttachmentMeta
        {
            Id        = Guid.Parse(id),
            NoteId    = noteId,
            Filename  = filename,
            MimeType  = mimeType,
            Size      = bytes.Length,
            CreatedAt = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task DeleteAsync(Guid attachmentId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE attachments SET deleted_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  attachmentId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteAsync(Guid attachmentId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "DELETE FROM attachments WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", attachmentId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static AttachmentMeta MapMeta(SqliteDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        NoteId    = r.GetGuid(1),
        Filename  = r.GetString(2),
        MimeType  = r.GetString(3),
        Size      = r.GetInt64(4),
        CreatedAt = r.GetDateTime(5),
        DeletedAt = r.GetNullableDateTime(6)
    };
}
