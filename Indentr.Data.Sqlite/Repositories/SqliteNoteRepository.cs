using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Sqlite.Repositories;

public class SqliteNoteRepository(string dbPath) : INoteRepository
{
    private const string Cols =
        "id, parent_id, is_root, title, content, content_hash, owner_id, sort_order, created_at, updated_at, created_by, is_private, deleted_at";

    // ── Public interface ──────────────────────────────────────────────────────

    public async Task<Note?> GetByIdAsync(Guid id)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            $"SELECT {Cols} FROM notes WHERE id = @id AND deleted_at IS NULL", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<Note?> GetRootAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            $"SELECT {Cols} FROM notes WHERE is_root = 1 AND created_by = @uid AND deleted_at IS NULL", conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<IEnumerable<NoteTreeNode>> GetChildrenAsync(Guid parentId, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            @"SELECT n.id, n.parent_id, n.title, n.sort_order,
                     EXISTS(SELECT 1 FROM notes c WHERE c.parent_id = n.id
                            AND (c.created_by = @uid OR c.is_private = 0)
                            AND c.deleted_at IS NULL) AS has_children,
                     n.created_by, n.is_private
              FROM notes n
              WHERE n.parent_id = @parentId
                AND (n.created_by = @uid OR n.is_private = 0)
                AND n.deleted_at IS NULL
              ORDER BY n.sort_order, n.title", conn);
        cmd.Parameters.AddWithValue("@parentId", parentId.ToString());
        cmd.Parameters.AddWithValue("@uid",      userId.ToString());
        var nodes = new List<NoteTreeNode>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            nodes.Add(new NoteTreeNode
            {
                Id          = r.GetGuid(0),
                ParentId    = r.GetNullableGuid(1),
                Title       = r.GetString(2),
                SortOrder   = r.GetInt32(3),
                HasChildren = r.GetBool(4),
                CreatedBy   = r.GetGuid(5),
                IsPrivate   = r.GetBool(6)
            });
        return nodes;
    }

    public async Task<IEnumerable<Note>> GetOrphansAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            $@"SELECT {Cols} FROM notes
               WHERE parent_id IS NULL AND is_root = 0
                 AND deleted_at IS NULL
                 AND (created_by = @uid OR is_private = 0)
                 AND NOT EXISTS (SELECT 1 FROM kanban_cards WHERE note_id = notes.id)
                 AND NOT EXISTS (SELECT 1 FROM notes linker
                                 WHERE linker.content LIKE '%note:' || notes.id || '%'
                                   AND linker.deleted_at IS NULL)
               ORDER BY title", conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        return await ReadNotes(cmd);
    }

    public async Task<IEnumerable<Note>> SearchAsync(string query, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        // SQLite doesn't have tsvector; use LIKE search on title and content.
        await using var cmd = new SqliteCommand(
            $@"SELECT {Cols} FROM notes
               WHERE (title LIKE @q OR content LIKE @q)
                 AND (created_by = @uid OR is_private = 0)
                 AND deleted_at IS NULL
               ORDER BY
                 CASE WHEN title LIKE @q THEN 0 ELSE 1 END,
                 title
               LIMIT 50", conn);
        cmd.Parameters.AddWithValue("@q",   $"%{query}%");
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        return await ReadNotes(cmd);
    }

    public async Task<Note> CreateAsync(Note note)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        if (note.Id == Guid.Empty)     note.Id       = Guid.NewGuid();
        if (note.CreatedBy == Guid.Empty) note.CreatedBy = note.OwnerId;
        note.ContentHash = ComputeHash(note.Content);

        var now = SqliteHelper.UtcNow();
        await using var cmd = new SqliteCommand(
            @"INSERT INTO notes (id, parent_id, is_root, title, content, content_hash,
                                 owner_id, created_by, is_private, sort_order, created_at, updated_at)
              VALUES (@id, @parentId, @isRoot, @title, @content, @hash,
                      @ownerId, @createdBy, @isPrivate, @sortOrder, @now, @now)",
            conn);
        cmd.Parameters.AddWithValue("@id",        note.Id.ToString());
        cmd.Parameters.AddWithValue("@parentId",  note.ParentId.HasValue ? note.ParentId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@isRoot",    note.IsRoot   ? 1 : 0);
        cmd.Parameters.AddWithValue("@title",     note.Title);
        cmd.Parameters.AddWithValue("@content",   note.Content);
        cmd.Parameters.AddWithValue("@hash",      note.ContentHash);
        cmd.Parameters.AddWithValue("@ownerId",   note.OwnerId.ToString());
        cmd.Parameters.AddWithValue("@createdBy", note.CreatedBy.ToString());
        cmd.Parameters.AddWithValue("@isPrivate", note.IsPrivate ? 1 : 0);
        cmd.Parameters.AddWithValue("@sortOrder", note.SortOrder);
        cmd.Parameters.AddWithValue("@now",       now);
        await cmd.ExecuteNonQueryAsync();

        var ts = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.RoundtripKind);
        note.CreatedAt = ts;
        note.UpdatedAt = ts;
        return note;
    }

    public async Task<SaveResult> SaveAsync(Note note, string originalHash, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        await using var check = new SqliteCommand(
            "SELECT content_hash, parent_id, content, created_by, is_private FROM notes WHERE id = @id", conn);
        check.Parameters.AddWithValue("@id", note.Id.ToString());
        await using var cr = await check.ExecuteReaderAsync();
        if (!await cr.ReadAsync()) return SaveResult.Success;

        var storedHash = cr.GetString(0);
        var oldContent = cr.GetString(2);
        var createdBy  = cr.GetGuid(3);
        var isPrivate  = cr.GetBool(4);
        await cr.CloseAsync();

        // Private notes can only be saved by their creator.
        if (isPrivate && createdBy != userId)
            return SaveResult.Unauthorized;

        bool conflict = storedHash != originalHash;
        if (conflict)
        {
            await CreateAsync(new Note
            {
                ParentId  = note.ParentId,
                IsRoot    = false,
                Title     = $"⚠ CONFLICT: {note.Title}",
                Content   = oldContent,
                OwnerId   = note.OwnerId,
                SortOrder = note.SortOrder + 1
            });
        }

        var newHash = ComputeHash(note.Content);
        await using var save = new SqliteCommand(
            @"UPDATE notes SET title = @title, content = @content, content_hash = @hash,
                               owner_id = @ownerId, is_private = @isPrivate, updated_at = @now
              WHERE id = @id",
            conn);
        save.Parameters.AddWithValue("@title",     note.Title);
        save.Parameters.AddWithValue("@content",   note.Content);
        save.Parameters.AddWithValue("@hash",      newHash);
        save.Parameters.AddWithValue("@ownerId",   note.OwnerId.ToString());
        save.Parameters.AddWithValue("@isPrivate", note.IsPrivate ? 1 : 0);
        save.Parameters.AddWithValue("@now",       SqliteHelper.UtcNow());
        save.Parameters.AddWithValue("@id",        note.Id.ToString());
        await save.ExecuteNonQueryAsync();

        note.ContentHash = newHash;
        await SyncParentLinksAsync(conn, note.Id, oldContent, note.Content);
        return conflict ? SaveResult.Conflict : SaveResult.Success;
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE notes SET deleted_at = @now, updated_at = @now WHERE id = @id AND created_by = @uid", conn);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  id.ToString());
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<Note>> GetTrashedAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            $@"SELECT {Cols} FROM notes
               WHERE deleted_at IS NOT NULL
                 AND is_root = 0
                 AND (created_by = @uid OR is_private = 0)
               ORDER BY deleted_at DESC",
            conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        return await ReadNotes(cmd);
    }

    public async Task RestoreAsync(Guid id, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE notes SET deleted_at = NULL, updated_at = @now WHERE id = @id AND created_by = @uid", conn);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  id.ToString());
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteAsync(Guid id, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "DELETE FROM notes WHERE id = @id AND created_by = @uid", conn);
        cmd.Parameters.AddWithValue("@id",  id.ToString());
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Note>> GetSubtreeAsync(Guid rootId, Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        // SQLite supports WITH RECURSIVE since 3.8.3 (2013); bundled version is much newer.
        await using var cmd = new SqliteCommand(
            $@"WITH RECURSIVE subtree AS (
                   SELECT {Cols} FROM notes WHERE id = @rootId AND deleted_at IS NULL
                   UNION ALL
                   SELECT n.id, n.parent_id, n.is_root, n.title, n.content, n.content_hash,
                          n.owner_id, n.sort_order, n.created_at, n.updated_at,
                          n.created_by, n.is_private, n.deleted_at
                   FROM notes n
                   INNER JOIN subtree s ON n.parent_id = s.id
                   WHERE n.deleted_at IS NULL
                     AND (n.created_by = @uid OR n.is_private = 0)
               )
               SELECT {Cols} FROM subtree",
            conn);
        cmd.Parameters.AddWithValue("@rootId", rootId.ToString());
        cmd.Parameters.AddWithValue("@uid",    userId.ToString());
        return await ReadNotes(cmd);
    }

    public async Task<IReadOnlyList<Guid>> UpdateLinkTitlesAsync(Guid noteId, string newTitle)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        await using var find = new SqliteCommand(
            "SELECT id, content FROM notes WHERE content LIKE @p AND id != @id", conn);
        find.Parameters.AddWithValue("@p",  $"%note:{noteId}%");
        find.Parameters.AddWithValue("@id", noteId.ToString());

        var pattern = new Regex(
            $@"\[([^\]]*)\]\(note:{Regex.Escape(noteId.ToString())}\)",
            RegexOptions.IgnoreCase);

        var toUpdate = new List<(string Id, string Content, string Hash)>();
        await using var fr = await find.ExecuteReaderAsync();
        while (await fr.ReadAsync())
        {
            var id      = fr.GetString(0);
            var content = fr.GetString(1);
            var updated = pattern.Replace(content, _ => $"[{newTitle}](note:{noteId})");
            if (updated != content)
                toUpdate.Add((id, updated, ComputeHash(updated)));
        }
        await fr.CloseAsync();

        var now = SqliteHelper.UtcNow();
        foreach (var (id, content, hash) in toUpdate)
        {
            await using var upd = new SqliteCommand(
                "UPDATE notes SET content = @content, content_hash = @hash, updated_at = @now WHERE id = @id",
                conn);
            upd.Parameters.AddWithValue("@content", content);
            upd.Parameters.AddWithValue("@hash",    hash);
            upd.Parameters.AddWithValue("@now",     now);
            upd.Parameters.AddWithValue("@id",      id);
            await upd.ExecuteNonQueryAsync();
        }

        return toUpdate.ConvertAll(x => Guid.Parse(x.Id));
    }

    public async Task EnsureRootExistsAsync(Guid ownerId)
    {
        if (await GetRootAsync(ownerId) is null)
            await CreateAsync(new Note
            {
                IsRoot    = true,
                Title     = "Root",
                OwnerId   = ownerId,
                CreatedBy = ownerId
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex NoteRefPattern = new(
        @"\(note:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static HashSet<Guid> ExtractNoteRefs(string content)
    {
        var refs = new HashSet<Guid>();
        foreach (Match m in NoteRefPattern.Matches(content))
            if (Guid.TryParse(m.Groups[1].Value, out var g))
                refs.Add(g);
        return refs;
    }

    private static async Task SyncParentLinksAsync(
        SqliteConnection conn, Guid savedNoteId, string oldContent, string newContent)
    {
        var oldRefs = ExtractNoteRefs(oldContent);
        var newRefs = ExtractNoteRefs(newContent);
        var pid     = savedNoteId.ToString();

        foreach (var addedId in newRefs.Except(oldRefs))
        {
            await using var adopt = new SqliteCommand(
                @"UPDATE notes SET parent_id = @pid
                  WHERE id = @id AND parent_id IS NULL AND is_root = 0
                    AND deleted_at IS NULL
                    AND NOT EXISTS (SELECT 1 FROM kanban_cards WHERE note_id = notes.id)",
                conn);
            adopt.Parameters.AddWithValue("@pid", pid);
            adopt.Parameters.AddWithValue("@id",  addedId.ToString());
            await adopt.ExecuteNonQueryAsync();
        }

        var candidates = oldRefs.Except(newRefs).ToHashSet();

        await using var childCmd = new SqliteCommand(
            "SELECT id FROM notes WHERE parent_id = @pid AND is_root = 0 AND deleted_at IS NULL", conn);
        childCmd.Parameters.AddWithValue("@pid", pid);
        await using var childR = await childCmd.ExecuteReaderAsync();
        while (await childR.ReadAsync())
            candidates.Add(Guid.Parse(childR.GetString(0)));
        await childR.CloseAsync();

        foreach (var noteId in candidates)
        {
            await using var linkerCmd = new SqliteCommand(
                "SELECT id FROM notes WHERE content LIKE @p AND deleted_at IS NULL LIMIT 1", conn);
            linkerCmd.Parameters.AddWithValue("@p", $"%note:{noteId}%");
            var linkerRaw = await linkerCmd.ExecuteScalarAsync();

            if (linkerRaw is null)
            {
                await using var orphan = new SqliteCommand(
                    "UPDATE notes SET parent_id = NULL WHERE id = @id AND is_root = 0", conn);
                orphan.Parameters.AddWithValue("@id", noteId.ToString());
                await orphan.ExecuteNonQueryAsync();
            }
            else
            {
                await using var reparent = new SqliteCommand(
                    @"UPDATE notes SET parent_id = @newPid
                      WHERE id = @id AND parent_id = @oldPid AND is_root = 0",
                    conn);
                reparent.Parameters.AddWithValue("@newPid", (string)linkerRaw);
                reparent.Parameters.AddWithValue("@id",     noteId.ToString());
                reparent.Parameters.AddWithValue("@oldPid", pid);
                await reparent.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<List<Note>> ReadNotes(SqliteCommand cmd)
    {
        var notes = new List<Note>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            notes.Add(Map(r));
        return notes;
    }

    private static Note Map(SqliteDataReader r) => new()
    {
        Id          = r.GetGuid(0),
        ParentId    = r.GetNullableGuid(1),
        IsRoot      = r.GetBool(2),
        Title       = r.GetString(3),
        Content     = r.GetString(4),
        ContentHash = r.GetString(5),
        OwnerId     = r.GetGuid(6),
        SortOrder   = r.GetInt32(7),
        CreatedAt   = r.GetDateTime(8),
        UpdatedAt   = r.GetDateTime(9),
        CreatedBy   = r.GetGuid(10),
        IsPrivate   = r.GetBool(11),
        DeletedAt   = r.GetNullableDateTime(12)
    };

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
