using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Organiz.Core.Interfaces;
using Organiz.Core.Models;

namespace Organiz.Data.Repositories;

public class NoteRepository(string connectionString) : INoteRepository
{
    private const string SelectColumns =
        "id, parent_id, is_root, title, content, content_hash, owner_id, sort_order, created_at, updated_at, created_by, is_private";

    public async Task<Note?> GetByIdAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM notes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapNote(r) : null;
    }

    public async Task<Note?> GetRootAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM notes WHERE is_root = TRUE AND created_by = @userId", conn);
        cmd.Parameters.AddWithValue("userId", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapNote(r) : null;
    }

    public async Task<IEnumerable<NoteTreeNode>> GetChildrenAsync(Guid parentId, Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT n.id, n.parent_id, n.title, n.sort_order,
                     EXISTS(SELECT 1 FROM notes c WHERE c.parent_id = n.id
                            AND (c.created_by = @userId OR c.is_private = FALSE)) AS has_children,
                     n.created_by, n.is_private
              FROM notes n
              WHERE n.parent_id = @parentId
                AND (n.created_by = @userId OR n.is_private = FALSE)
              ORDER BY n.sort_order, n.title", conn);
        cmd.Parameters.AddWithValue("parentId", parentId);
        cmd.Parameters.AddWithValue("userId", userId);
        var nodes = new List<NoteTreeNode>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            nodes.Add(new NoteTreeNode
            {
                Id          = r.GetGuid(0),
                ParentId    = r.IsDBNull(1) ? null : r.GetGuid(1),
                Title       = r.GetString(2),
                SortOrder   = r.GetInt32(3),
                HasChildren = r.GetBoolean(4),
                CreatedBy   = r.GetGuid(5),
                IsPrivate   = r.GetBoolean(6)
            });
        return nodes;
    }

    public async Task<IEnumerable<Note>> GetOrphansAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Show orphans that belong to the current user or are public.
        await using var cmd = new NpgsqlCommand(
            $@"SELECT {SelectColumns} FROM notes
               WHERE parent_id IS NULL AND is_root = FALSE
                 AND (created_by = @userId OR is_private = FALSE)
               ORDER BY title", conn);
        cmd.Parameters.AddWithValue("userId", userId);
        return await ReadNotes(cmd);
    }

    public async Task<IEnumerable<Note>> SearchAsync(string query, Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Exclude private notes created by someone else.
        await using var cmd = new NpgsqlCommand(
            $@"SELECT {SelectColumns} FROM notes
               WHERE search_vector @@ plainto_tsquery('english', @query)
                 AND (created_by = @userId OR is_private = FALSE)
               ORDER BY ts_rank(search_vector, plainto_tsquery('english', @query)) DESC
               LIMIT 50", conn);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("userId", userId);
        return await ReadNotes(cmd);
    }

    public async Task<Note> CreateAsync(Note note)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        if (note.Id == Guid.Empty) note.Id = Guid.NewGuid();
        note.ContentHash = ComputeHash(note.Content);

        // created_by is the immutable creator; fall back to OwnerId if not explicitly set.
        if (note.CreatedBy == Guid.Empty) note.CreatedBy = note.OwnerId;

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO notes (id, parent_id, is_root, title, content, content_hash,
                                 owner_id, created_by, is_private, sort_order)
              VALUES (@id, @parentId, @isRoot, @title, @content, @hash,
                      @ownerId, @createdBy, @isPrivate, @sortOrder)
              RETURNING created_at, updated_at", conn);
        cmd.Parameters.AddWithValue("id", note.Id);
        cmd.Parameters.AddWithValue("parentId", (object?)note.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isRoot", note.IsRoot);
        cmd.Parameters.AddWithValue("title", note.Title);
        cmd.Parameters.AddWithValue("content", note.Content);
        cmd.Parameters.AddWithValue("hash", note.ContentHash);
        cmd.Parameters.AddWithValue("ownerId", note.OwnerId);
        cmd.Parameters.AddWithValue("createdBy", note.CreatedBy);
        cmd.Parameters.AddWithValue("isPrivate", note.IsPrivate);
        cmd.Parameters.AddWithValue("sortOrder", note.SortOrder);

        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            note.CreatedAt = r.GetDateTime(0);
            note.UpdatedAt = r.GetDateTime(1);
        }
        return note;
    }

    public async Task<SaveResult> SaveAsync(Note note, string originalHash)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Read current state
        await using var checkCmd = new NpgsqlCommand(
            "SELECT content_hash, parent_id, content FROM notes WHERE id = @id", conn);
        checkCmd.Parameters.AddWithValue("id", note.Id);
        await using var checkReader = await checkCmd.ExecuteReaderAsync();
        if (!await checkReader.ReadAsync()) return SaveResult.Success;

        string storedHash = checkReader.GetString(0);
        Guid?  parentId   = checkReader.IsDBNull(1) ? null : checkReader.GetGuid(1);
        string oldContent = checkReader.GetString(2);
        await checkReader.CloseAsync();

        if (storedHash != originalHash)
        {
            // Conflict: create a sibling conflict note
            var conflictNote = new Note
            {
                ParentId = parentId,
                IsRoot = false,
                Title = $"[CONFLICT] {note.Title}",
                Content = note.Content,
                OwnerId = note.OwnerId,
                SortOrder = note.SortOrder + 1
            };
            await CreateAsync(conflictNote);
            return SaveResult.Conflict;
        }

        var newHash = ComputeHash(note.Content);
        await using var saveCmd = new NpgsqlCommand(
            @"UPDATE notes SET title = @title, content = @content, content_hash = @hash,
                               owner_id = @ownerId, is_private = @isPrivate, updated_at = NOW()
              WHERE id = @id", conn);
        saveCmd.Parameters.AddWithValue("title", note.Title);
        saveCmd.Parameters.AddWithValue("content", note.Content);
        saveCmd.Parameters.AddWithValue("hash", newHash);
        saveCmd.Parameters.AddWithValue("ownerId", note.OwnerId);
        saveCmd.Parameters.AddWithValue("isPrivate", note.IsPrivate);
        saveCmd.Parameters.AddWithValue("id", note.Id);
        await saveCmd.ExecuteNonQueryAsync();

        note.ContentHash = newHash;

        // Keep parent_id in sync with the link graph
        await SyncParentLinksAsync(conn, note.Id, oldContent, note.Content);

        return SaveResult.Success;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Children are orphaned by the ON DELETE SET NULL FK
        await using var cmd = new NpgsqlCommand("DELETE FROM notes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Guid>> UpdateLinkTitlesAsync(Guid noteId, string newTitle)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Find every note (other than noteId itself) that contains a link to noteId.
        await using var findCmd = new NpgsqlCommand(
            "SELECT id, content FROM notes WHERE content LIKE @p AND id != @id", conn);
        findCmd.Parameters.AddWithValue("p", $"%note:{noteId}%");
        findCmd.Parameters.AddWithValue("id", noteId);

        var pattern = new Regex(
            $@"\[([^\]]*)\]\(note:{Regex.Escape(noteId.ToString())}\)",
            RegexOptions.IgnoreCase);

        var toUpdate = new List<(Guid Id, string Content, string Hash)>();
        await using var reader = await findCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id      = reader.GetGuid(0);
            var content = reader.GetString(1);
            // MatchEvaluator lambda ensures newTitle is treated as a literal string
            var updated = pattern.Replace(content, _ => $"[{newTitle}](note:{noteId})");
            if (updated != content)
                toUpdate.Add((id, updated, ComputeHash(updated)));
        }
        await reader.CloseAsync();

        foreach (var (id, content, hash) in toUpdate)
        {
            await using var updateCmd = new NpgsqlCommand(
                "UPDATE notes SET content = @content, content_hash = @hash, updated_at = NOW() WHERE id = @id",
                conn);
            updateCmd.Parameters.AddWithValue("content", content);
            updateCmd.Parameters.AddWithValue("hash", hash);
            updateCmd.Parameters.AddWithValue("id", id);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return toUpdate.ConvertAll(x => x.Id);
    }

    public async Task EnsureRootExistsAsync(Guid ownerId)
    {
        var root = await GetRootAsync(ownerId);
        if (root is null)
            await CreateAsync(new Note
            {
                IsRoot    = true,
                Title     = "Root",
                OwnerId   = ownerId,
                CreatedBy = ownerId
            });
    }

    // --- helpers ---

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

    // Keeps parent_id in sync with the link graph every time a note is saved.
    // parent_id is the single source of truth for tree structure, but it is always
    // derived from in-app links — never set directly from the UI.
    private static async Task SyncParentLinksAsync(
        NpgsqlConnection conn, Guid savedNoteId, string oldContent, string newContent)
    {
        var oldRefs = ExtractNoteRefs(oldContent);
        var newRefs = ExtractNoteRefs(newContent);

        // Adopt: a link was added → if the target is currently orphaned, give it this note as parent
        foreach (var addedId in newRefs.Except(oldRefs))
        {
            await using var adoptCmd = new NpgsqlCommand(
                "UPDATE notes SET parent_id = @pid WHERE id = @id AND parent_id IS NULL AND is_root = FALSE", conn);
            adoptCmd.Parameters.AddWithValue("pid", savedNoteId);
            adoptCmd.Parameters.AddWithValue("id", addedId);
            await adoptCmd.ExecuteNonQueryAsync();
        }

        // Orphan candidates: explicitly removed refs, plus all current structural children.
        // Including structural children catches notes whose link was inserted into the
        // editor but the parent was never saved to DB before the link was deleted.
        var candidates = oldRefs.Except(newRefs).ToHashSet();

        await using var childCmd = new NpgsqlCommand(
            "SELECT id FROM notes WHERE parent_id = @pid AND is_root = FALSE", conn);
        childCmd.Parameters.AddWithValue("pid", savedNoteId);
        await using var childReader = await childCmd.ExecuteReaderAsync();
        while (await childReader.ReadAsync())
            candidates.Add(childReader.GetGuid(0));
        await childReader.CloseAsync();

        foreach (var noteId in candidates)
        {
            // Find any note that still links to this candidate.
            await using var linkerCmd = new NpgsqlCommand(
                "SELECT id FROM notes WHERE content LIKE @p LIMIT 1", conn);
            linkerCmd.Parameters.AddWithValue("p", $"%note:{noteId}%");
            var linkerIdRaw = await linkerCmd.ExecuteScalarAsync();

            if (linkerIdRaw is null)
            {
                // Nothing links to it any more: orphan it.
                await using var orphanCmd = new NpgsqlCommand(
                    "UPDATE notes SET parent_id = NULL WHERE id = @id AND is_root = FALSE", conn);
                orphanCmd.Parameters.AddWithValue("id", noteId);
                await orphanCmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Something still links to it. If savedNoteId was its parent but no longer
                // links to it, transfer the parent to the remaining linker.
                await using var reparentCmd = new NpgsqlCommand(
                    @"UPDATE notes SET parent_id = @newPid
                      WHERE id = @id AND parent_id = @oldPid AND is_root = FALSE", conn);
                reparentCmd.Parameters.AddWithValue("newPid", (Guid)linkerIdRaw);
                reparentCmd.Parameters.AddWithValue("id", noteId);
                reparentCmd.Parameters.AddWithValue("oldPid", savedNoteId);
                await reparentCmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<List<Note>> ReadNotes(NpgsqlCommand cmd)
    {
        var notes = new List<Note>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            notes.Add(MapNote(r));
        return notes;
    }

    private static Note MapNote(NpgsqlDataReader r) => new()
    {
        Id          = r.GetGuid(0),
        ParentId    = r.IsDBNull(1) ? null : r.GetGuid(1),
        IsRoot      = r.GetBoolean(2),
        Title       = r.GetString(3),
        Content     = r.GetString(4),
        ContentHash = r.GetString(5),
        OwnerId     = r.GetGuid(6),
        SortOrder   = r.GetInt32(7),
        CreatedAt   = r.GetDateTime(8),
        UpdatedAt   = r.GetDateTime(9),
        CreatedBy   = r.GetGuid(10),
        IsPrivate   = r.GetBoolean(11)
    };

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
