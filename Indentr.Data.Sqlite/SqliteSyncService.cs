using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using Indentr.Core.Interfaces;
using Indentr.Data;

namespace Indentr.Data.Sqlite;

/// <summary>
/// Sync service for a SQLite local database and a PostgreSQL remote database.
/// Push reads from SQLite and writes to PostgreSQL.
/// Pull reads from PostgreSQL and writes to SQLite.
/// The PostgreSQL write methods are intentionally similar to those in SyncService;
/// they could be extracted to a shared helper in a future refactor.
/// </summary>
public class SqliteSyncService(string localDbPath, string? remoteCs, Guid userId) : ISyncService
{
    private readonly string  _localDbPath = localDbPath;
    private readonly string? _remoteCs    = remoteCs;
    private readonly Guid    _userId      = userId;

    private record SyncLogEntry(long Id, string EntityType, Guid EntityId, string Operation);
    private record UpsertGroup(string EntityType, Guid EntityId, List<long> SyncLogIds);

    private static readonly string[] UpsertOrder =
        ["users", "notes", "attachments", "scratchpads", "kanban_boards", "kanban_columns", "kanban_cards"];

    private static readonly string[] DeleteOrder =
        ["kanban_cards", "kanban_columns", "kanban_boards", "scratchpads", "attachments", "notes", "users"];

    private static readonly HashSet<string> KnownEntityTypes =
    [
        "users", "notes", "scratchpads", "attachments",
        "kanban_boards", "kanban_columns", "kanban_cards"
    ];

    private static readonly TimeSpan PullSafetyBuffer = TimeSpan.FromSeconds(30);

    // ── ISyncService ──────────────────────────────────────────────────────────

    public async Task<DateTimeOffset> GetLastSyncedAtAsync()
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT last_synced_at FROM sync_state WHERE id = 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is string s
            ? DateTimeOffset.Parse(s, null, DateTimeStyles.RoundtripKind)
            : DateTimeOffset.MinValue;
    }

    private async Task SetLastSyncedAtAsync(DateTimeOffset ts)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE sync_state SET last_synced_at = @ts WHERE id = 1", conn);
        cmd.Parameters.AddWithValue("@ts", ts.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SyncResult> SyncOnceAsync()
    {
        if (_remoteCs is null) return SyncResult.Offline;

        var connectError = await ConnectionStringBuilder.TryConnectAsync(_remoteCs);
        if (connectError is not null) return SyncResult.Offline;

        try
        {
            await new DatabaseMigrator(_remoteCs).MigrateAsync();
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Remote migration failed: {ex.Message}");
        }

        try
        {
            await PushAsync();
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Push failed: {ex.Message}");
        }

        var syncStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var lastSyncedAt = await GetLastSyncedAtAsync();
            await using var remote = new NpgsqlConnection(_remoteCs);
            await remote.OpenAsync();
            syncStartedAt = await GetRemoteClockAsync(remote);
            await PullAsync(remote, lastSyncedAt);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Pull failed: {ex.Message}");
        }

        try { await SetLastSyncedAtAsync(syncStartedAt); } catch { /* non-fatal */ }

        return SyncResult.Success;
    }

    private static async Task<DateTimeOffset> GetRemoteClockAsync(NpgsqlConnection remote)
    {
        await using var cmd = new NpgsqlCommand("SELECT NOW()", remote);
        var result = await cmd.ExecuteScalarAsync();
        return result is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;
    }

    // ── Push (SQLite → PostgreSQL) ────────────────────────────────────────────

    private async Task PushAsync()
    {
        await DeduplicateSyncLogAsync();
        var entries = await ReadPendingSyncLogAsync();
        if (entries.Count == 0) return;

        var upsertGroups = entries
            .Where(e => e.Operation != "DELETE")
            .GroupBy(e => (e.EntityType, e.EntityId))
            .Select(g => new UpsertGroup(g.Key.EntityType, g.Key.EntityId, g.Select(x => x.Id).ToList()))
            .ToList();

        var dels      = entries.Where(e => e.Operation == "DELETE").ToList();
        var processed = new List<long>();

        await using var remote = new NpgsqlConnection(_remoteCs!);
        await remote.OpenAsync();

        var userIdRemap = await BuildUserIdRemapAsync(remote);

        foreach (var type in UpsertOrder)
        {
            foreach (var group in upsertGroups.Where(g => g.EntityType == type))
            {
                await PushEntityAsync(remote, group.EntityType, group.EntityId, userIdRemap);
                processed.AddRange(group.SyncLogIds);
            }
        }

        var pushedNoteIds = upsertGroups.Where(g => g.EntityType == "notes").Select(g => g.EntityId).ToList();
        var noteIdRemap   = await BuildNoteIdRemapAsync(remote, pushedNoteIds, userIdRemap);
        await FixRemoteNoteParentIdsAsync(remote, pushedNoteIds, noteIdRemap);

        foreach (var type in DeleteOrder)
        {
            foreach (var entry in dels.Where(e => e.EntityType == type))
            {
                await PushDeleteAsync(remote, entry.EntityType, entry.EntityId);
                processed.Add(entry.Id);
            }
        }

        await DeleteSyncLogEntriesAsync(processed);
    }

    private async Task DeduplicateSyncLogAsync()
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            @"DELETE FROM sync_log
              WHERE operation IN ('INSERT', 'UPDATE')
                AND EXISTS (
                    SELECT 1 FROM sync_log sl2
                    WHERE sl2.entity_type = sync_log.entity_type
                      AND sl2.entity_id   = sync_log.entity_id
                      AND sl2.operation   IN ('INSERT', 'UPDATE')
                      AND sl2.id          > sync_log.id
                )",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<SyncLogEntry>> ReadPendingSyncLogAsync()
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, entity_type, entity_id, operation FROM sync_log ORDER BY id", conn);
        var entries = new List<SyncLogEntry>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            entries.Add(new SyncLogEntry(
                r.GetInt64(0), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetString(3)));
        return entries;
    }

    // Build UUID remap: local SQLite userId → remote PostgreSQL userId (by username).
    private async Task<Dictionary<Guid, Guid>> BuildUserIdRemapAsync(NpgsqlConnection remote)
    {
        // Read all remote users by username.
        var dstByUsername = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand("SELECT id, username FROM users", remote))
        await using (var r   = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                dstByUsername[r.GetString(1)] = r.GetGuid(0);

        // Read local SQLite users and find mismatches.
        var remap = new Dictionary<Guid, Guid>();
        await using var conn    = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var srcCmd  = new SqliteCommand("SELECT id, username FROM users", conn);
        await using var srcR    = await srcCmd.ExecuteReaderAsync();
        while (await srcR.ReadAsync())
        {
            var srcId    = Guid.Parse(srcR.GetString(0));
            var username = srcR.GetString(1);
            if (dstByUsername.TryGetValue(username, out var dstId) && dstId != srcId)
                remap[srcId] = dstId;
        }
        return remap;
    }

    private static Guid Remap(Dictionary<Guid, Guid> map, Guid id) =>
        map.TryGetValue(id, out var v) ? v : id;

    // Find local root notes that were skipped on remote (duplicate root for same user).
    private async Task<Dictionary<Guid, Guid>> BuildNoteIdRemapAsync(
        NpgsqlConnection remote, IEnumerable<Guid> pushedNoteIds, Dictionary<Guid, Guid> userIdRemap)
    {
        var remap = new Dictionary<Guid, Guid>();
        if (userIdRemap.Count == 0) return remap;

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();

        foreach (var noteId in pushedNoteIds)
        {
            await using var localCmd = new SqliteCommand(
                "SELECT is_root, created_by FROM notes WHERE id = @id", conn);
            localCmd.Parameters.AddWithValue("@id", noteId.ToString());
            await using var lr = await localCmd.ExecuteReaderAsync();
            if (!await lr.ReadAsync()) continue;
            var isRoot    = lr.GetInt32(0) != 0;
            var createdBy = Guid.Parse(lr.GetString(1));
            await lr.CloseAsync();

            if (!isRoot) continue;
            var remoteCreatedBy = Remap(userIdRemap, createdBy);
            if (remoteCreatedBy == createdBy) continue;

            await using var remoteCmd = new NpgsqlCommand(
                "SELECT id FROM notes WHERE is_root = TRUE AND created_by = @cb AND deleted_at IS NULL",
                remote);
            remoteCmd.Parameters.AddWithValue("cb", remoteCreatedBy);
            if (await remoteCmd.ExecuteScalarAsync() is Guid remoteRootId && remoteRootId != noteId)
                remap[noteId] = remoteRootId;
        }
        return remap;
    }

    private async Task PushEntityAsync(
        NpgsqlConnection remote, string entityType, Guid id,
        Dictionary<Guid, Guid> userIdRemap)
    {
        switch (entityType)
        {
            case "users":         await PushUserAsync(remote, id); break;
            case "notes":         await PushNoteAsync(remote, id, userIdRemap); break;
            case "attachments":   await PushAttachmentAsync(remote, id, userIdRemap); break;
            case "scratchpads":   await PushScratchpadAsync(remote, id, userIdRemap); break;
            case "kanban_boards": await PushKanbanBoardAsync(remote, id, userIdRemap); break;
            case "kanban_columns":await PushKanbanColumnAsync(remote, id); break;
            case "kanban_cards":  await PushKanbanCardAsync(remote, id); break;
        }
    }

    private async Task PushUserAsync(NpgsqlConnection remote, Guid id)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT username, created_at FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var username  = r.GetString(0);
        var createdAt = r.GetDateTime(1);
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO users (id, username, created_at)
              SELECT @id, @username, @createdAt
              WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = @username AND id <> @id)
              ON CONFLICT (id) DO UPDATE SET username = EXCLUDED.username",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("username",  username);
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task PushNoteAsync(
        NpgsqlConnection remote, Guid id, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT is_root, title, content, content_hash, owner_id, created_by, is_private, sort_order, created_at, updated_at, deleted_at FROM notes WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var row = (
            IsRoot:    r.GetInt32(0) != 0,
            Title:     r.GetString(1),
            Content:   r.GetString(2),
            Hash:      r.GetString(3),
            OwnerId:   Guid.Parse(r.GetString(4)),
            CreatedBy: Guid.Parse(r.GetString(5)),
            IsPrivate: r.GetInt32(6) != 0,
            SortOrder: r.GetInt32(7),
            CreatedAt: r.GetDateTime(8),
            UpdatedAt: r.GetDateTime(9),
            DeletedAt: r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at, deleted_at)
              SELECT @id, NULL, @isRoot, @title, @content, @hash,
                     @ownerId, @createdBy, @isPrivate, @sortOrder, @createdAt, @updatedAt, @deletedAt
              WHERE NOT (@isRoot AND EXISTS (
                  SELECT 1 FROM notes WHERE is_root = TRUE AND created_by = @createdBy AND id <> @id
              ))
              ON CONFLICT (id) DO UPDATE SET
                is_root      = EXCLUDED.is_root,
                title        = EXCLUDED.title,
                content      = EXCLUDED.content,
                content_hash = EXCLUDED.content_hash,
                owner_id     = EXCLUDED.owner_id,
                is_private   = EXCLUDED.is_private,
                sort_order   = EXCLUDED.sort_order,
                updated_at   = EXCLUDED.updated_at,
                deleted_at   = EXCLUDED.deleted_at",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("isRoot",    row.IsRoot);
        upsert.Parameters.AddWithValue("title",     row.Title);
        upsert.Parameters.AddWithValue("content",   row.Content);
        upsert.Parameters.AddWithValue("hash",      row.Hash);
        upsert.Parameters.AddWithValue("ownerId",   Remap(userIdRemap, row.OwnerId));
        upsert.Parameters.AddWithValue("createdBy", Remap(userIdRemap, row.CreatedBy));
        upsert.Parameters.AddWithValue("isPrivate", row.IsPrivate);
        upsert.Parameters.AddWithValue("sortOrder", row.SortOrder);
        upsert.Parameters.AddWithValue("createdAt", row.CreatedAt);
        upsert.Parameters.AddWithValue("updatedAt", row.UpdatedAt);
        upsert.Parameters.AddWithValue("deletedAt", (object?)row.DeletedAt ?? DBNull.Value);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task PushAttachmentAsync(
        NpgsqlConnection remote, Guid id, Dictionary<Guid, Guid> userIdRemap)
    {
        // Read the BLOB from SQLite.
        byte[]    bytes;
        Guid      noteId;
        string    filename, mimeType;
        long      size;
        DateTime  createdAt;
        DateTime? deletedAt;

        await using (var conn = SqliteHelper.Open(_localDbPath))
        {
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "SELECT note_id, filename, mime_type, size, created_at, deleted_at, data FROM attachments WHERE id = @id",
                conn);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return;
            noteId    = Guid.Parse(r.GetString(0));
            filename  = r.GetString(1);
            mimeType  = r.GetString(2);
            size      = r.GetInt64(3);
            createdAt = r.GetDateTime(4);
            deletedAt = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5);
            bytes     = r.GetFieldValue<byte[]>(6);
        }

        // Write to PostgreSQL remote using large objects (same as SyncService).
        await using var tx = await remote.BeginTransactionAsync();

        await using (var del = new NpgsqlCommand("DELETE FROM attachments WHERE id = @id", remote))
        {
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync();
        }

        uint newOid;
        await using (var loCmd = new NpgsqlCommand("SELECT lo_from_bytea(0, @data)", remote))
        {
            loCmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = bytes });
            await using var loR = await loCmd.ExecuteReaderAsync();
            await loR.ReadAsync();
            newOid = loR.GetFieldValue<uint>(0);
        }

        await using (var ins = new NpgsqlCommand(
            @"INSERT INTO attachments (id, note_id, lo_oid, filename, mime_type, size, created_at, deleted_at)
              VALUES (@id, @noteId, @oid, @filename, @mimeType, @size, @createdAt, @deletedAt)",
            remote))
        {
            ins.Parameters.AddWithValue("id",        id);
            ins.Parameters.AddWithValue("noteId",    noteId);
            ins.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = newOid });
            ins.Parameters.AddWithValue("filename",  filename);
            ins.Parameters.AddWithValue("mimeType",  mimeType);
            ins.Parameters.AddWithValue("size",      size);
            ins.Parameters.AddWithValue("createdAt", createdAt);
            ins.Parameters.AddWithValue("deletedAt", (object?)deletedAt ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private async Task PushScratchpadAsync(
        NpgsqlConnection remote, Guid id, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT user_id, content, content_hash, updated_at FROM scratchpads WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var userId    = Guid.Parse(r.GetString(0));
        var content   = r.GetString(1);
        var hash      = r.GetString(2);
        var updatedAt = r.GetDateTime(3);
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO scratchpads (id, user_id, content, content_hash, updated_at)
              VALUES (@id, @userId, @content, @hash, @updatedAt)
              ON CONFLICT (user_id) DO UPDATE SET
                content      = EXCLUDED.content,
                content_hash = EXCLUDED.content_hash,
                updated_at   = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("userId",    Remap(userIdRemap, userId));
        upsert.Parameters.AddWithValue("content",   content);
        upsert.Parameters.AddWithValue("hash",      hash);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task PushKanbanBoardAsync(
        NpgsqlConnection remote, Guid id, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (title, ownerId, createdAt, updatedAt, deletedAt) =
            (r.GetString(0), Guid.Parse(r.GetString(1)), r.GetDateTime(2), r.GetDateTime(3),
             r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at, deleted_at)
              VALUES (@id, @title, @ownerId, @createdAt, @updatedAt, @deletedAt)
              ON CONFLICT (id) DO UPDATE SET
                title      = EXCLUDED.title,
                updated_at = EXCLUDED.updated_at,
                deleted_at = EXCLUDED.deleted_at",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("title",     title);
        upsert.Parameters.AddWithValue("ownerId",   Remap(userIdRemap, ownerId));
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        upsert.Parameters.AddWithValue("deletedAt", (object?)deletedAt ?? DBNull.Value);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task PushKanbanColumnAsync(NpgsqlConnection remote, Guid id)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT board_id, title, sort_order, updated_at, deleted_at FROM kanban_columns WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (boardId, title, sortOrder, updatedAt, deletedAt) =
            (Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt32(2), r.GetDateTime(3),
             r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at, deleted_at)
              VALUES (@id, @boardId, @title, @sortOrder, @updatedAt, @deletedAt)
              ON CONFLICT (id) DO UPDATE SET
                board_id   = EXCLUDED.board_id,
                title      = EXCLUDED.title,
                sort_order = EXCLUDED.sort_order,
                updated_at = EXCLUDED.updated_at,
                deleted_at = EXCLUDED.deleted_at",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("boardId",   boardId);
        upsert.Parameters.AddWithValue("title",     title);
        upsert.Parameters.AddWithValue("sortOrder", sortOrder);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        upsert.Parameters.AddWithValue("deletedAt", (object?)deletedAt ?? DBNull.Value);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task PushKanbanCardAsync(NpgsqlConnection remote, Guid id)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT column_id, title, note_id, sort_order, created_at, updated_at, deleted_at FROM kanban_cards WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (colId, title, noteId, sortOrder, createdAt, updatedAt, deletedAt) = (
            Guid.Parse(r.GetString(0)), r.GetString(1),
            r.IsDBNull(2) ? (Guid?)null : Guid.Parse(r.GetString(2)),
            r.GetInt32(3), r.GetDateTime(4), r.GetDateTime(5),
            r.IsDBNull(6) ? (DateTime?)null : r.GetDateTime(6));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_cards
                (id, column_id, title, note_id, sort_order, created_at, updated_at, deleted_at)
              VALUES (@id, @colId, @title, @noteId, @sortOrder, @createdAt, @updatedAt, @deletedAt)
              ON CONFLICT (id) DO UPDATE SET
                column_id  = EXCLUDED.column_id,
                title      = EXCLUDED.title,
                note_id    = EXCLUDED.note_id,
                sort_order = EXCLUDED.sort_order,
                updated_at = EXCLUDED.updated_at,
                deleted_at = EXCLUDED.deleted_at",
            remote);
        upsert.Parameters.AddWithValue("id",        id);
        upsert.Parameters.AddWithValue("colId",     colId);
        upsert.Parameters.AddWithValue("title",     title);
        upsert.Parameters.AddWithValue("noteId",    (object?)noteId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("sortOrder", sortOrder);
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        upsert.Parameters.AddWithValue("deletedAt", (object?)deletedAt ?? DBNull.Value);
        await upsert.ExecuteNonQueryAsync();
    }

    private static async Task PushDeleteAsync(NpgsqlConnection remote, string entityType, Guid id)
    {
        if (!KnownEntityTypes.Contains(entityType)) return;
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {entityType} WHERE id = @id", remote);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* row may already be absent on remote */ }
    }

    private async Task FixRemoteNoteParentIdsAsync(
        NpgsqlConnection remote, IEnumerable<Guid> noteIds, Dictionary<Guid, Guid> noteIdRemap)
    {
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var id in noteIds)
        {
            await using var cmd = new SqliteCommand(
                "SELECT parent_id FROM notes WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            var raw      = await cmd.ExecuteScalarAsync();
            var parentId = raw is string s ? (Guid?)Guid.Parse(s) : (Guid?)null;
            if (parentId.HasValue) parentId = Remap(noteIdRemap, parentId.Value);

            await using var upd = new NpgsqlCommand(
                "UPDATE notes SET parent_id = @pid WHERE id = @id", remote);
            upd.Parameters.AddWithValue("pid", (object?)parentId ?? DBNull.Value);
            upd.Parameters.AddWithValue("id",  id);
            await upd.ExecuteNonQueryAsync();
        }
    }

    private async Task DeleteSyncLogEntriesAsync(List<long> ids)
    {
        if (ids.Count == 0) return;
        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        // SQLite doesn't support ANY(@ids); use a parameterised IN list.
        var placeholders = string.Join(",", ids.Select((_, i) => $"@p{i}"));
        await using var cmd = new SqliteCommand(
            $"DELETE FROM sync_log WHERE id IN ({placeholders})", conn);
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Pull (PostgreSQL → SQLite) ────────────────────────────────────────────

    private record RemoteNote(
        Guid Id, Guid? ParentId, bool IsRoot, string Title, string Content, string ContentHash,
        Guid OwnerId, Guid CreatedBy, bool IsPrivate, int SortOrder,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? DeletedAt);

    private async Task PullAsync(NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        var safetyFilterSince = lastSyncedAt - PullSafetyBuffer;

        // Build remap: remote user UUID → local user UUID (by username match).
        var userIdRemap    = await BuildPullUserIdRemapAsync(remote);
        var localToRemote  = await BuildUserIdRemapAsync(remote); // local → remote
        var remoteUserId   = Remap(localToRemote, _userId);

        await PullUsersAsync(remote, userIdRemap);
        await PullNotesAsync(remote, lastSyncedAt, safetyFilterSince, remoteUserId, userIdRemap);
        await PullAttachmentsAsync(remote, safetyFilterSince);
        await PullScratchpadsAsync(remote, safetyFilterSince, userIdRemap);
        await PullKanbanBoardsAsync(remote, safetyFilterSince, lastSyncedAt, userIdRemap);
        await PullKanbanColumnsAsync(remote, safetyFilterSince, lastSyncedAt);
        await PullKanbanCardsAsync(remote, safetyFilterSince, lastSyncedAt);
        await PullRemoteDeletesAsync(remote, lastSyncedAt);
    }

    // Build remap: remote userId → local userId (reverse direction from push remap).
    private async Task<Dictionary<Guid, Guid>> BuildPullUserIdRemapAsync(NpgsqlConnection remote)
    {
        var localByUsername = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using var conn    = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        await using var localCmd = new SqliteCommand("SELECT id, username FROM users", conn);
        await using var localR   = await localCmd.ExecuteReaderAsync();
        while (await localR.ReadAsync())
            localByUsername[localR.GetString(1)] = Guid.Parse(localR.GetString(0));
        await localR.CloseAsync();

        var remap = new Dictionary<Guid, Guid>();
        await using var remoteCmd = new NpgsqlCommand("SELECT id, username FROM users", remote);
        await using var remoteR   = await remoteCmd.ExecuteReaderAsync();
        while (await remoteR.ReadAsync())
        {
            var remoteId = remoteR.GetGuid(0);
            var username = remoteR.GetString(1);
            if (localByUsername.TryGetValue(username, out var localId) && localId != remoteId)
                remap[remoteId] = localId;
        }
        return remap;
    }

    private async Task PullUsersAsync(NpgsqlConnection remote, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, username, created_at FROM users", remote);
        await using var r   = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, string Username, DateTime CreatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetString(1), r.GetDateTime(2)));
        await r.CloseAsync();

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var (id, username, createdAt) in rows)
        {
            await using var upsert = new SqliteCommand(
                @"INSERT OR IGNORE INTO users (id, username, created_at)
                  SELECT @id, @username, @createdAt
                  WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = @username AND id != @id)",
                conn);
            upsert.Parameters.AddWithValue("@id",        id.ToString());
            upsert.Parameters.AddWithValue("@username",  username);
            upsert.Parameters.AddWithValue("@createdAt", SqliteHelper.Iso(createdAt));
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private async Task PullNotesAsync(
        NpgsqlConnection remote, DateTimeOffset lastSyncedAt, DateTimeOffset safetyFilterSince,
        Guid remoteUserId, Dictionary<Guid, Guid> userIdRemap)
    {
        var remoteNotes = new List<RemoteNote>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT id, parent_id, is_root, title, content, content_hash,
                     owner_id, created_by, is_private, sort_order, created_at, updated_at, deleted_at
              FROM notes
              WHERE updated_at > @since
                AND (created_by = @userId OR is_private = FALSE)",
            remote))
        {
            cmd.Parameters.AddWithValue("since",  safetyFilterSince.UtcDateTime);
            cmd.Parameters.AddWithValue("userId", remoteUserId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                remoteNotes.Add(new RemoteNote(
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetGuid(1),
                    r.GetBoolean(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetGuid(6), r.GetGuid(7), r.GetBoolean(8), r.GetInt32(9),
                    r.GetDateTime(10), r.GetDateTime(11),
                    r.IsDBNull(12) ? null : r.GetDateTime(12)));
        }

        if (remoteNotes.Count == 0) return;

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();

        // Build note ID remap for remote roots that map to existing local roots.
        var pullNoteIdRemap = new Dictionary<Guid, Guid>();
        foreach (var rn in remoteNotes.Where(n => n.IsRoot))
        {
            var localCreatedBy = Remap(userIdRemap, rn.CreatedBy);
            await using var findLocal = new SqliteCommand(
                "SELECT id FROM notes WHERE is_root = 1 AND created_by = @cb AND id != @id AND deleted_at IS NULL",
                conn);
            findLocal.Parameters.AddWithValue("@cb", localCreatedBy.ToString());
            findLocal.Parameters.AddWithValue("@id", rn.Id.ToString());
            if (await findLocal.ExecuteScalarAsync() is string localRootIdStr)
                pullNoteIdRemap[rn.Id] = Guid.Parse(localRootIdStr);
        }

        var parentIdFixes = new List<(Guid Id, Guid? ParentId)>();

        foreach (var rn in remoteNotes)
        {
            await using var checkCmd = new SqliteCommand(
                "SELECT updated_at, content_hash FROM notes WHERE id = @id", conn);
            checkCmd.Parameters.AddWithValue("@id", rn.Id.ToString());
            await using var checkR = await checkCmd.ExecuteReaderAsync();
            var exists        = await checkR.ReadAsync();
            var localUpdatedAt = exists ? checkR.GetDateTime(0) : default;
            var localHash      = exists ? checkR.GetString(1)   : null;
            await checkR.CloseAsync();

            if (!exists)
            {
                if (rn.IsRoot && pullNoteIdRemap.TryGetValue(rn.Id, out var localRootId))
                {
                    await AdoptRemoteRootAsync(conn, rn, localRootId, userIdRemap);
                    pullNoteIdRemap.Remove(rn.Id);
                }
                else
                {
                    await InsertNoteFromRemoteAsync(conn, rn, userIdRemap);
                    parentIdFixes.Add((rn.Id, rn.ParentId));
                }
            }
            else if (rn.ContentHash == localHash)
            {
                // Identical content — already in sync; skip.
            }
            else if (rn.UpdatedAt <= lastSyncedAt.UtcDateTime)
            {
                if (localUpdatedAt <= lastSyncedAt.UtcDateTime)
                {
                    await UpdateNoteFromRemoteAsync(conn, rn, userIdRemap);
                    parentIdFixes.Add((rn.Id, rn.ParentId));
                }
            }
            else if (localUpdatedAt <= lastSyncedAt.UtcDateTime)
            {
                await UpdateNoteFromRemoteAsync(conn, rn, userIdRemap);
                parentIdFixes.Add((rn.Id, rn.ParentId));
            }
            else
            {
                await CreateConflictNoteAsync(conn, rn, userIdRemap);
            }
        }

        // Pass 2: restore parent_ids now that all notes exist locally.
        foreach (var (id, parentId) in parentIdFixes)
        {
            var resolved = parentId.HasValue ? Remap(pullNoteIdRemap, parentId.Value) : (Guid?)null;
            await using var fix = new SqliteCommand(
                "UPDATE notes SET parent_id = @pid WHERE id = @id", conn);
            fix.Parameters.AddWithValue("@pid", resolved.HasValue ? resolved.Value.ToString() : DBNull.Value);
            fix.Parameters.AddWithValue("@id",  id.ToString());
            await fix.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertNoteFromRemoteAsync(
        SqliteConnection conn, RemoteNote rn, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var cmd = new SqliteCommand(
            @"INSERT OR IGNORE INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at, deleted_at)
              SELECT @id, NULL, @isRoot, @title, @content, @hash,
                     @ownerId, @createdBy, @isPrivate, @sortOrder, @createdAt, @updatedAt, @deletedAt
              WHERE NOT (@isRoot AND EXISTS (
                  SELECT 1 FROM notes WHERE is_root = 1 AND created_by = @createdBy AND id != @id
              ))",
            conn);
        AddNoteParams(cmd, rn, userIdRemap);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task UpdateNoteFromRemoteAsync(
        SqliteConnection conn, RemoteNote rn, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var cmd = new SqliteCommand(
            @"UPDATE notes SET
                title        = @title,
                content      = @content,
                content_hash = @hash,
                owner_id     = @ownerId,
                is_private   = @isPrivate,
                sort_order   = @sortOrder,
                updated_at   = @updatedAt,
                deleted_at   = @deletedAt
              WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@title",     rn.Title);
        cmd.Parameters.AddWithValue("@content",   rn.Content);
        cmd.Parameters.AddWithValue("@hash",      rn.ContentHash);
        cmd.Parameters.AddWithValue("@ownerId",   Remap(userIdRemap, rn.OwnerId).ToString());
        cmd.Parameters.AddWithValue("@isPrivate", rn.IsPrivate ? 1 : 0);
        cmd.Parameters.AddWithValue("@sortOrder", rn.SortOrder);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteHelper.Iso(rn.UpdatedAt));
        cmd.Parameters.AddWithValue("@deletedAt", SqliteHelper.IsoOrNull(rn.DeletedAt));
        cmd.Parameters.AddWithValue("@id",        rn.Id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AdoptRemoteRootAsync(
        SqliteConnection conn, RemoteNote rn, Guid localRootId, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var repoint = new SqliteCommand(
            "UPDATE notes SET parent_id = @newId WHERE parent_id = @oldId", conn);
        repoint.Parameters.AddWithValue("@newId", rn.Id.ToString());
        repoint.Parameters.AddWithValue("@oldId", localRootId.ToString());
        await repoint.ExecuteNonQueryAsync();

        await using var adopt = new SqliteCommand(
            @"UPDATE notes SET
                id           = @newId,
                title        = @title,
                content      = @content,
                content_hash = @hash,
                owner_id     = @ownerId,
                is_private   = @isPrivate,
                sort_order   = @sortOrder,
                updated_at   = @updatedAt,
                deleted_at   = @deletedAt
              WHERE id = @oldId",
            conn);
        adopt.Parameters.AddWithValue("@newId",     rn.Id.ToString());
        adopt.Parameters.AddWithValue("@title",     rn.Title);
        adopt.Parameters.AddWithValue("@content",   rn.Content);
        adopt.Parameters.AddWithValue("@hash",      rn.ContentHash);
        adopt.Parameters.AddWithValue("@ownerId",   Remap(userIdRemap, rn.OwnerId).ToString());
        adopt.Parameters.AddWithValue("@isPrivate", rn.IsPrivate ? 1 : 0);
        adopt.Parameters.AddWithValue("@sortOrder", rn.SortOrder);
        adopt.Parameters.AddWithValue("@updatedAt", SqliteHelper.Iso(rn.UpdatedAt));
        adopt.Parameters.AddWithValue("@deletedAt", SqliteHelper.IsoOrNull(rn.DeletedAt));
        adopt.Parameters.AddWithValue("@oldId",     localRootId.ToString());
        await adopt.ExecuteNonQueryAsync();
    }

    private static async Task CreateConflictNoteAsync(
        SqliteConnection conn, RemoteNote rn, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var readCmd = new SqliteCommand(
            "SELECT parent_id, sort_order FROM notes WHERE id = @id", conn);
        readCmd.Parameters.AddWithValue("@id", rn.Id.ToString());
        await using var pr = await readCmd.ExecuteReaderAsync();
        if (!await pr.ReadAsync()) return;
        var parentId  = pr.IsDBNull(0) ? (Guid?)null : Guid.Parse(pr.GetString(0));
        var sortOrder = pr.GetInt32(1);
        await pr.CloseAsync();

        await using var usernameCmd = new SqliteCommand(
            "SELECT username FROM users WHERE id = @id", conn);
        usernameCmd.Parameters.AddWithValue("@id", Remap(userIdRemap, rn.OwnerId).ToString());
        var username  = (string?)await usernameCmd.ExecuteScalarAsync() ?? "unknown";
        var timestamp = rn.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var title     = $"⚠ CONFLICT: {rn.Title} (by {username} on {timestamp})";

        // Guard against duplicate conflict notes.
        await using var existsCmd = new SqliteCommand(
            @"SELECT 1 FROM notes
              WHERE parent_id IS @parentId
                AND title = @title
              LIMIT 1",
            conn);
        existsCmd.Parameters.AddWithValue("@parentId", parentId.HasValue ? parentId.Value.ToString() : DBNull.Value);
        existsCmd.Parameters.AddWithValue("@title", title);
        if (await existsCmd.ExecuteScalarAsync() is not null) return;

        var now = SqliteHelper.UtcNow();
        await using var ins = new SqliteCommand(
            @"INSERT INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at)
              VALUES
                (@id, @parentId, 0, @title, @content, @hash,
                 @ownerId, @ownerId, @isPrivate, @sortOrder, @now, @now)",
            conn);
        ins.Parameters.AddWithValue("@id",        Guid.NewGuid().ToString());
        ins.Parameters.AddWithValue("@parentId",  parentId.HasValue ? parentId.Value.ToString() : DBNull.Value);
        ins.Parameters.AddWithValue("@title",     title);
        ins.Parameters.AddWithValue("@content",   rn.Content);
        ins.Parameters.AddWithValue("@hash",      rn.ContentHash);
        ins.Parameters.AddWithValue("@ownerId",   Remap(userIdRemap, rn.OwnerId).ToString());
        ins.Parameters.AddWithValue("@isPrivate", rn.IsPrivate ? 1 : 0);
        ins.Parameters.AddWithValue("@sortOrder", sortOrder + 1);
        ins.Parameters.AddWithValue("@now",       now);
        await ins.ExecuteNonQueryAsync();

        await using var touchCmd = new SqliteCommand(
            "UPDATE notes SET updated_at = @now WHERE id = @id", conn);
        touchCmd.Parameters.AddWithValue("@now", now);
        touchCmd.Parameters.AddWithValue("@id",  rn.Id.ToString());
        await touchCmd.ExecuteNonQueryAsync();
    }

    private static void AddNoteParams(SqliteCommand cmd, RemoteNote rn, Dictionary<Guid, Guid> userIdRemap)
    {
        cmd.Parameters.AddWithValue("@id",        rn.Id.ToString());
        cmd.Parameters.AddWithValue("@isRoot",    rn.IsRoot    ? 1 : 0);
        cmd.Parameters.AddWithValue("@title",     rn.Title);
        cmd.Parameters.AddWithValue("@content",   rn.Content);
        cmd.Parameters.AddWithValue("@hash",      rn.ContentHash);
        cmd.Parameters.AddWithValue("@ownerId",   Remap(userIdRemap, rn.OwnerId).ToString());
        cmd.Parameters.AddWithValue("@createdBy", Remap(userIdRemap, rn.CreatedBy).ToString());
        cmd.Parameters.AddWithValue("@isPrivate", rn.IsPrivate ? 1 : 0);
        cmd.Parameters.AddWithValue("@sortOrder", rn.SortOrder);
        cmd.Parameters.AddWithValue("@createdAt", SqliteHelper.Iso(rn.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", SqliteHelper.Iso(rn.UpdatedAt));
        cmd.Parameters.AddWithValue("@deletedAt", SqliteHelper.IsoOrNull(rn.DeletedAt));
    }

    private async Task PullAttachmentsAsync(NpgsqlConnection remote, DateTimeOffset safetyFilterSince)
    {
        var newRows = new List<(Guid Id, Guid NoteId)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, note_id FROM attachments WHERE created_at > @since", remote))
        {
            cmd.Parameters.AddWithValue("since", safetyFilterSince.UtcDateTime);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                newRows.Add((r.GetGuid(0), r.GetGuid(1)));
        }

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();

        foreach (var (id, noteId) in newRows)
        {
            await using var existsCmd = new SqliteCommand(
                "SELECT 1 FROM attachments WHERE id = @id", conn);
            existsCmd.Parameters.AddWithValue("@id", id.ToString());
            if (await existsCmd.ExecuteScalarAsync() is not null) continue;

            await using var noteCmd = new SqliteCommand(
                "SELECT 1 FROM notes WHERE id = @id", conn);
            noteCmd.Parameters.AddWithValue("@id", noteId.ToString());
            if (await noteCmd.ExecuteScalarAsync() is null) continue;

            try
            {
                await PullAttachmentFromRemoteAsync(remote, conn, id, noteId);
            }
            catch { /* retry next sync */ }
        }

        // Propagate soft-deletes from remote.
        var localActiveIds = new List<string>();
        await using (var cmd = new SqliteCommand(
            "SELECT id FROM attachments WHERE deleted_at IS NULL", conn))
        {
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                localActiveIds.Add(r.GetString(0));
        }

        if (localActiveIds.Count == 0) return;

        var placeholders = string.Join(",", localActiveIds.Select((_, i) => $"@p{i}"));
        var remoteDeleted = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT id FROM attachments WHERE id = ANY(@ids) AND deleted_at IS NOT NULL",
            remote))
        {
            cmd.Parameters.AddWithValue("ids", localActiveIds.Select(Guid.Parse).ToArray());
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                remoteDeleted.Add(r.GetGuid(0));
        }

        foreach (var id in remoteDeleted)
        {
            await using var softDel = new SqliteCommand(
                "UPDATE attachments SET deleted_at = @now WHERE id = @id AND deleted_at IS NULL", conn);
            softDel.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
            softDel.Parameters.AddWithValue("@id",  id.ToString());
            await softDel.ExecuteNonQueryAsync();
        }
    }

    private static async Task PullAttachmentFromRemoteAsync(
        NpgsqlConnection remote, SqliteConnection local, Guid id, Guid noteId)
    {
        byte[]    bytes;
        string    filename, mimeType;
        long      size;
        DateTime  createdAt;
        DateTime? deletedAt;

        await using (var tx = await remote.BeginTransactionAsync())
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT filename, mime_type, size, created_at, deleted_at, lo_get(lo_oid) FROM attachments WHERE id = @id",
                remote);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) { await tx.RollbackAsync(); return; }
            filename  = r.GetString(0);
            mimeType  = r.GetString(1);
            size      = r.GetInt64(2);
            createdAt = r.GetDateTime(3);
            deletedAt = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4);
            bytes     = r.GetFieldValue<byte[]>(5);
            await tx.CommitAsync();
        }

        await using var ins = new SqliteCommand(
            "INSERT OR IGNORE INTO attachments (id, note_id, data, filename, mime_type, size, created_at, deleted_at) " +
            "VALUES (@id, @noteId, @data, @filename, @mimeType, @size, @createdAt, @deletedAt)",
            local);
        ins.Parameters.AddWithValue("@id",        id.ToString());
        ins.Parameters.AddWithValue("@noteId",    noteId.ToString());
        ins.Parameters.AddWithValue("@data",      bytes);
        ins.Parameters.AddWithValue("@filename",  filename);
        ins.Parameters.AddWithValue("@mimeType",  mimeType);
        ins.Parameters.AddWithValue("@size",      size);
        ins.Parameters.AddWithValue("@createdAt", SqliteHelper.Iso(createdAt));
        ins.Parameters.AddWithValue("@deletedAt", SqliteHelper.IsoOrNull(deletedAt));
        await ins.ExecuteNonQueryAsync();
    }

    private async Task PullScratchpadsAsync(
        NpgsqlConnection remote, DateTimeOffset safetyFilterSince, Dictionary<Guid, Guid> userIdRemap)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, user_id, content, content_hash, updated_at FROM scratchpads WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", safetyFilterSince.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, Guid UserId, string Content, string Hash, DateTime UpdatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        await r.CloseAsync();

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var (id, userId, content, hash, updatedAt) in rows)
        {
            await using var upsert = new SqliteCommand(
                @"INSERT INTO scratchpads (id, user_id, content, content_hash, updated_at)
                  VALUES (@id, @userId, @content, @hash, @updatedAt)
                  ON CONFLICT(user_id) DO UPDATE SET
                    content      = EXCLUDED.content,
                    content_hash = EXCLUDED.content_hash,
                    updated_at   = EXCLUDED.updated_at",
                conn);
            upsert.Parameters.AddWithValue("@id",        id.ToString());
            upsert.Parameters.AddWithValue("@userId",    Remap(userIdRemap, userId).ToString());
            upsert.Parameters.AddWithValue("@content",   content);
            upsert.Parameters.AddWithValue("@hash",      hash);
            upsert.Parameters.AddWithValue("@updatedAt", SqliteHelper.Iso(updatedAt));
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private async Task PullKanbanBoardsAsync(
        NpgsqlConnection remote, DateTimeOffset safetyFilterSince, DateTimeOffset lastSyncedAt,
        Dictionary<Guid, Guid> userIdRemap)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", safetyFilterSince.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, string, Guid, DateTime, DateTime, DateTime?)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetDateTime(3), r.GetDateTime(4),
                      r.IsDBNull(5) ? null : r.GetDateTime(5)));
        await r.CloseAsync();

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var (id, title, ownerId, createdAt, updatedAt, deletedAt) in rows)
        {
            await using var upsert = new SqliteCommand(
                @"INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at, deleted_at)
                  VALUES (@id, @title, @ownerId, @createdAt, @updatedAt, @deletedAt)
                  ON CONFLICT(id) DO UPDATE SET
                    title      = EXCLUDED.title,
                    updated_at = EXCLUDED.updated_at,
                    deleted_at = EXCLUDED.deleted_at
                  WHERE kanban_boards.updated_at <= @lastSynced",
                conn);
            upsert.Parameters.AddWithValue("@id",         id.ToString());
            upsert.Parameters.AddWithValue("@title",      title);
            upsert.Parameters.AddWithValue("@ownerId",    Remap(userIdRemap, ownerId).ToString());
            upsert.Parameters.AddWithValue("@createdAt",  SqliteHelper.Iso(createdAt));
            upsert.Parameters.AddWithValue("@updatedAt",  SqliteHelper.Iso(updatedAt));
            upsert.Parameters.AddWithValue("@deletedAt",  SqliteHelper.IsoOrNull(deletedAt));
            upsert.Parameters.AddWithValue("@lastSynced", SqliteHelper.Iso(lastSyncedAt.UtcDateTime));
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private async Task PullKanbanColumnsAsync(
        NpgsqlConnection remote, DateTimeOffset safetyFilterSince, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, board_id, title, sort_order, updated_at, deleted_at FROM kanban_columns WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", safetyFilterSince.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, Guid, string, int, DateTime, DateTime?)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetDateTime(4),
                      r.IsDBNull(5) ? null : r.GetDateTime(5)));
        await r.CloseAsync();

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var (id, boardId, title, sortOrder, updatedAt, deletedAt) in rows)
        {
            try
            {
                await using var upsert = new SqliteCommand(
                    @"INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at, deleted_at)
                      VALUES (@id, @boardId, @title, @sortOrder, @updatedAt, @deletedAt)
                      ON CONFLICT(id) DO UPDATE SET
                        board_id   = EXCLUDED.board_id,
                        title      = EXCLUDED.title,
                        sort_order = EXCLUDED.sort_order,
                        updated_at = EXCLUDED.updated_at,
                        deleted_at = EXCLUDED.deleted_at
                      WHERE kanban_columns.updated_at <= @lastSynced",
                    conn);
                upsert.Parameters.AddWithValue("@id",         id.ToString());
                upsert.Parameters.AddWithValue("@boardId",    boardId.ToString());
                upsert.Parameters.AddWithValue("@title",      title);
                upsert.Parameters.AddWithValue("@sortOrder",  sortOrder);
                upsert.Parameters.AddWithValue("@updatedAt",  SqliteHelper.Iso(updatedAt));
                upsert.Parameters.AddWithValue("@deletedAt",  SqliteHelper.IsoOrNull(deletedAt));
                upsert.Parameters.AddWithValue("@lastSynced", SqliteHelper.Iso(lastSyncedAt.UtcDateTime));
                await upsert.ExecuteNonQueryAsync();
            }
            catch { /* parent board not yet local — retry next cycle */ }
        }
    }

    private async Task PullKanbanCardsAsync(
        NpgsqlConnection remote, DateTimeOffset safetyFilterSince, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, column_id, title, note_id, sort_order, created_at, updated_at, deleted_at FROM kanban_cards WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", safetyFilterSince.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, Guid, string, Guid?, int, DateTime, DateTime, DateTime?)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2),
                      r.IsDBNull(3) ? null : r.GetGuid(3),
                      r.GetInt32(4), r.GetDateTime(5), r.GetDateTime(6),
                      r.IsDBNull(7) ? null : r.GetDateTime(7)));
        await r.CloseAsync();

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        foreach (var (id, colId, title, noteId, sortOrder, createdAt, updatedAt, deletedAt) in rows)
        {
            try
            {
                await using var upsert = new SqliteCommand(
                    @"INSERT INTO kanban_cards
                        (id, column_id, title, note_id, sort_order, created_at, updated_at, deleted_at)
                      VALUES (@id, @colId, @title, @noteId, @sortOrder, @createdAt, @updatedAt, @deletedAt)
                      ON CONFLICT(id) DO UPDATE SET
                        column_id  = EXCLUDED.column_id,
                        title      = EXCLUDED.title,
                        note_id    = EXCLUDED.note_id,
                        sort_order = EXCLUDED.sort_order,
                        updated_at = EXCLUDED.updated_at,
                        deleted_at = EXCLUDED.deleted_at
                      WHERE kanban_cards.updated_at <= @lastSynced",
                    conn);
                upsert.Parameters.AddWithValue("@id",         id.ToString());
                upsert.Parameters.AddWithValue("@colId",      colId.ToString());
                upsert.Parameters.AddWithValue("@title",      title);
                upsert.Parameters.AddWithValue("@noteId",     noteId.HasValue ? noteId.Value.ToString() : DBNull.Value);
                upsert.Parameters.AddWithValue("@sortOrder",  sortOrder);
                upsert.Parameters.AddWithValue("@createdAt",  SqliteHelper.Iso(createdAt));
                upsert.Parameters.AddWithValue("@updatedAt",  SqliteHelper.Iso(updatedAt));
                upsert.Parameters.AddWithValue("@deletedAt",  SqliteHelper.IsoOrNull(deletedAt));
                upsert.Parameters.AddWithValue("@lastSynced", SqliteHelper.Iso(lastSyncedAt.UtcDateTime));
                await upsert.ExecuteNonQueryAsync();
            }
            catch { /* parent column not yet local — retry next cycle */ }
        }
    }

    private async Task PullRemoteDeletesAsync(NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        // Collect locally-pending IDs so we don't delete local creates.
        var pendingIds = new HashSet<Guid>();
        await using (var conn = SqliteHelper.Open(_localDbPath))
        {
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand("SELECT DISTINCT entity_id FROM sync_log", conn);
            await using var r   = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                pendingIds.Add(Guid.Parse(r.GetString(0)));
        }

        // Non-root notes and kanban hierarchy only (mirrors SyncService.DeleteDetectionOrder).
        await ApplyRemoteDeletesForTableAsync(remote, "kanban_boards", lastSyncedAt, pendingIds, null);
        await ApplyRemoteDeletesForTableAsync(remote, "kanban_columns", lastSyncedAt, pendingIds, null);
        await ApplyRemoteDeletesForTableAsync(remote, "kanban_cards",   lastSyncedAt, pendingIds, null);
        await ApplyRemoteDeletesForTableAsync(remote, "notes",          lastSyncedAt, pendingIds, "is_root = 0");
    }

    private async Task ApplyRemoteDeletesForTableAsync(
        NpgsqlConnection remote, string tableName, DateTimeOffset lastSyncedAt,
        HashSet<Guid> pendingIds, string? localExtraWhere)
    {
        if (!KnownEntityTypes.Contains(tableName)) return;

        var remoteIds = new HashSet<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT id FROM {tableName}", remote))
        await using (var r   = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                remoteIds.Add(r.GetGuid(0));

        await using var conn = SqliteHelper.Open(_localDbPath);
        await conn.OpenAsync();
        var where = localExtraWhere is null ? "" : $" WHERE {localExtraWhere}";
        var localRows = new Dictionary<Guid, DateTime>();
        await using (var cmd = new SqliteCommand($"SELECT id, updated_at FROM {tableName}{where}", conn))
        await using (var r   = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                localRows[Guid.Parse(r.GetString(0))] = r.GetDateTime(1);

        foreach (var (localId, localUpdatedAt) in localRows)
        {
            if (remoteIds.Contains(localId)) continue;
            if (pendingIds.Contains(localId)) continue;
            if (localUpdatedAt > lastSyncedAt.UtcDateTime) continue;

            try
            {
                await using var del = new SqliteCommand(
                    $"DELETE FROM {tableName} WHERE id = @id", conn);
                del.Parameters.AddWithValue("@id", localId.ToString());
                await del.ExecuteNonQueryAsync();
            }
            catch { /* FK constraint: child may delete first on next pass */ }
        }
    }
}
