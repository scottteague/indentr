using Npgsql;
using NpgsqlTypes;
using Indentr.Core.Interfaces;

namespace Indentr.Data;

public class SyncService(string localConnectionString, string? remoteConnectionString) : ISyncService
{
    private readonly string  _localCs  = localConnectionString;
    private readonly string? _remoteCs = remoteConnectionString;

    // ── Internal types ────────────────────────────────────────────────────────

    private record SyncLogEntry(long Id, string EntityType, Guid EntityId, string Operation);

    // One group per unique (EntityType, EntityId) — all sync_log IDs for that entity
    // are collected so every entry gets cleaned up after a single push.
    private record UpsertGroup(string EntityType, Guid EntityId, List<long> SyncLogIds);

    private static readonly string[] UpsertOrder =
        ["users", "notes", "attachments", "scratchpads", "kanban_boards", "kanban_columns", "kanban_cards"];

    private static readonly string[] DeleteOrder =
        ["kanban_cards", "kanban_columns", "kanban_boards", "scratchpads", "attachments", "notes", "users"];

    // Whitelist used before interpolating entity_type into DELETE SQL.
    private static readonly HashSet<string> KnownEntityTypes =
    [
        "users", "notes", "scratchpads", "attachments",
        "kanban_boards", "kanban_columns", "kanban_cards"
    ];

    // ── ISyncService ─────────────────────────────────────────────────────────

    public async Task<DateTimeOffset> GetLastSyncedAtAsync()
    {
        await using var conn = new NpgsqlConnection(_localCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_synced_at FROM sync_state WHERE id = 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : DateTimeOffset.MinValue;
    }

    private async Task SetLastSyncedAtAsync(DateTimeOffset timestamp)
    {
        await using var conn = new NpgsqlConnection(_localCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE sync_state SET last_synced_at = @ts WHERE id = 1", conn);
        cmd.Parameters.AddWithValue("ts", timestamp.UtcDateTime);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SyncResult> SyncOnceAsync()
    {
        if (_remoteCs is null)
            return SyncResult.Offline;

        // 1. Verify remote is reachable.
        var connectError = await ConnectionStringBuilder.TryConnectAsync(_remoteCs);
        if (connectError is not null)
            return SyncResult.Offline;

        // 2. Ensure remote schema is current.
        try
        {
            await new DatabaseMigrator(_remoteCs).MigrateAsync();
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Remote migration failed: {ex.Message}");
        }

        // 3. Push local changes to remote.
        try
        {
            await using var local  = new NpgsqlConnection(_localCs);
            await using var remote = new NpgsqlConnection(_remoteCs);
            await local.OpenAsync();
            await remote.OpenAsync();
            await PushAsync(local, remote, _localCs, _remoteCs);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Push failed: {ex.Message}");
        }

        // 4. Pull remote changes to local.
        // Capture the start time before fetching the filter watermark so that anything
        // modified on remote while this cycle runs is included in the next sync's window.
        var syncStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var lastSyncedAt = await GetLastSyncedAtAsync();
            await using var local  = new NpgsqlConnection(_localCs);
            await using var remote = new NpgsqlConnection(_remoteCs);
            await local.OpenAsync();
            await remote.OpenAsync();
            await PullAsync(local, remote, lastSyncedAt);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Pull failed: {ex.Message}");
            // last_synced_at is NOT advanced — next sync retries from the same point.
        }

        // 5. Both phases succeeded — advance the watermark to when the pull started.
        //    This is syncStartedAt, not Now(), so any remote change that arrived during
        //    this cycle falls inside the next sync's updated_at > last_synced_at window.
        try
        {
            await SetLastSyncedAtAsync(syncStartedAt);
        }
        catch
        {
            // Non-fatal: data is correct on both sides. Worst case the next cycle
            // re-pulls already-applied rows, which the upserts handle idempotently.
        }

        return SyncResult.Success;
    }

    // ── Push ─────────────────────────────────────────────────────────────────

    private static async Task PushAsync(
        NpgsqlConnection local, NpgsqlConnection remote, string localCs, string remoteCs)
    {
        var entries = await ReadPendingSyncLogAsync(local);
        if (entries.Count == 0) return;

        // Group INSERT/UPDATE by (entity_type, entity_id). We always push the entity's
        // *current* local state, so multiple log entries for the same entity collapse to
        // one upsert. All their sync_log IDs are still collected for cleanup.
        var upsertGroups = entries
            .Where(e => e.Operation != "DELETE")
            .GroupBy(e => (e.EntityType, e.EntityId))
            .Select(g => new UpsertGroup(
                g.Key.EntityType, g.Key.EntityId,
                g.Select(x => x.Id).ToList()))
            .ToList();

        var dels      = entries.Where(e => e.Operation == "DELETE").ToList();
        var processed = new List<long>();

        // Upsert in FK dependency order.
        // Notes are pushed with parent_id = NULL on this pass to avoid FK ordering
        // issues when an entire new subtree is being synced. FixNoteParentIdsAsync
        // corrects parent_ids in a second pass once all notes exist on remote.
        foreach (var type in UpsertOrder)
        {
            foreach (var group in upsertGroups.Where(g => g.EntityType == type))
            {
                await UpsertEntityAsync(local, remote, localCs, remoteCs, group.EntityType, group.EntityId);
                processed.AddRange(group.SyncLogIds);
            }
        }

        // Second pass: restore correct parent_ids on all pushed notes.
        var pushedNoteIds = upsertGroups
            .Where(g => g.EntityType == "notes")
            .Select(g => g.EntityId);
        await FixNoteParentIdsAsync(local, remote, pushedNoteIds);

        // Delete in reverse FK dependency order (children before parents).
        foreach (var type in DeleteOrder)
        {
            foreach (var entry in dels.Where(e => e.EntityType == type))
            {
                await PushDeleteAsync(remote, entry.EntityType, entry.EntityId);
                processed.Add(entry.Id);
            }
        }

        await DeleteSyncLogEntriesAsync(local, processed);
    }

    private static async Task<List<SyncLogEntry>> ReadPendingSyncLogAsync(NpgsqlConnection local)
    {
        var entries = new List<SyncLogEntry>();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, entity_type, entity_id, operation FROM sync_log ORDER BY id",
            local);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            entries.Add(new SyncLogEntry(
                r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetString(3)));
        return entries;
    }

    private static Task UpsertEntityAsync(
        NpgsqlConnection local, NpgsqlConnection remote,
        string localCs, string remoteCs,
        string entityType, Guid id) =>
        entityType switch
        {
            "users"          => UpsertUserAsync(local, remote, id),
            "notes"          => UpsertNoteAsync(local, remote, id),
            "attachments"    => UpsertAttachmentAsync(localCs, remoteCs, id),
            "scratchpads"    => UpsertScratchpadAsync(local, remote, id),
            "kanban_boards"  => UpsertKanbanBoardAsync(local, remote, id),
            "kanban_columns" => UpsertKanbanColumnAsync(local, remote, id),
            "kanban_cards"   => UpsertKanbanCardAsync(local, remote, id),
            _                => Task.CompletedTask
        };

    // ── Per-entity upserts ────────────────────────────────────────────────────

    private static async Task UpsertUserAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT username, created_at FROM users WHERE id = @id", local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return; // deleted locally; DELETE entry will handle remote
        var (username, createdAt) = (r.GetString(0), r.GetDateTime(1));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO users (id, username, created_at)
              VALUES (@id, @username, @createdAt)
              ON CONFLICT (id) DO UPDATE SET username = EXCLUDED.username",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("username", username);
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        await upsert.ExecuteNonQueryAsync();
    }

    // Pass 1 of 2: upsert without parent_id (always NULL). FixNoteParentIdsAsync is pass 2.
    // search_vector is a GENERATED ALWAYS AS column and must be omitted from the INSERT list.
    private static async Task UpsertNoteAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT is_root, title, content, content_hash, owner_id, created_by,
                     is_private, sort_order, created_at, updated_at
              FROM notes WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return; // deleted locally; DELETE entry will handle remote
        var row = (
            IsRoot:    r.GetBoolean(0),  Title:     r.GetString(1),
            Content:   r.GetString(2),   Hash:      r.GetString(3),
            OwnerId:   r.GetGuid(4),     CreatedBy: r.GetGuid(5),
            IsPrivate: r.GetBoolean(6),  SortOrder: r.GetInt32(7),
            CreatedAt: r.GetDateTime(8), UpdatedAt: r.GetDateTime(9));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at)
              VALUES
                (@id, NULL, @isRoot, @title, @content, @hash,
                 @ownerId, @createdBy, @isPrivate, @sortOrder, @createdAt, @updatedAt)
              ON CONFLICT (id) DO UPDATE SET
                is_root      = EXCLUDED.is_root,
                title        = EXCLUDED.title,
                content      = EXCLUDED.content,
                content_hash = EXCLUDED.content_hash,
                owner_id     = EXCLUDED.owner_id,
                is_private   = EXCLUDED.is_private,
                sort_order   = EXCLUDED.sort_order,
                updated_at   = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("isRoot", row.IsRoot);
        upsert.Parameters.AddWithValue("title", row.Title);
        upsert.Parameters.AddWithValue("content", row.Content);
        upsert.Parameters.AddWithValue("hash", row.Hash);
        upsert.Parameters.AddWithValue("ownerId", row.OwnerId);
        upsert.Parameters.AddWithValue("createdBy", row.CreatedBy);
        upsert.Parameters.AddWithValue("isPrivate", row.IsPrivate);
        upsert.Parameters.AddWithValue("sortOrder", row.SortOrder);
        upsert.Parameters.AddWithValue("createdAt", row.CreatedAt);
        upsert.Parameters.AddWithValue("updatedAt", row.UpdatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    // Pass 2 of 2: set correct parent_ids now that all pushed notes exist on remote.
    private static async Task FixNoteParentIdsAsync(
        NpgsqlConnection local, NpgsqlConnection remote, IEnumerable<Guid> noteIds)
    {
        foreach (var id in noteIds)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT parent_id FROM notes WHERE id = @id", local);
            cmd.Parameters.AddWithValue("id", id);
            var raw      = await cmd.ExecuteScalarAsync();
            var parentId = raw is Guid g ? (Guid?)g : null;

            await using var update = new NpgsqlCommand(
                "UPDATE notes SET parent_id = @parentId WHERE id = @id", remote);
            update.Parameters.AddWithValue("parentId", (object?)parentId ?? DBNull.Value);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync();
        }
    }

    // Scratchpads conflict on user_id (UNIQUE), not id, because remote may have already
    // auto-created its own scratchpad row for the same user with a different UUID.
    private static async Task UpsertScratchpadAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT user_id, content, content_hash, updated_at FROM scratchpads WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (userId, content, hash, updatedAt) =
            (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDateTime(3));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO scratchpads (id, user_id, content, content_hash, updated_at)
              VALUES (@id, @userId, @content, @hash, @updatedAt)
              ON CONFLICT (user_id) DO UPDATE SET
                content      = EXCLUDED.content,
                content_hash = EXCLUDED.content_hash,
                updated_at   = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("userId", userId);
        upsert.Parameters.AddWithValue("content", content);
        upsert.Parameters.AddWithValue("hash", hash);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    private static async Task UpsertKanbanBoardAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT title, owner_id, created_at, updated_at FROM kanban_boards WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (title, ownerId, createdAt, updatedAt) =
            (r.GetString(0), r.GetGuid(1), r.GetDateTime(2), r.GetDateTime(3));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at)
              VALUES (@id, @title, @ownerId, @createdAt, @updatedAt)
              ON CONFLICT (id) DO UPDATE SET
                title      = EXCLUDED.title,
                updated_at = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("title", title);
        upsert.Parameters.AddWithValue("ownerId", ownerId);
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    private static async Task UpsertKanbanColumnAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT board_id, title, sort_order, updated_at FROM kanban_columns WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (boardId, title, sortOrder, updatedAt) =
            (r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetDateTime(3));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at)
              VALUES (@id, @boardId, @title, @sortOrder, @updatedAt)
              ON CONFLICT (id) DO UPDATE SET
                board_id   = EXCLUDED.board_id,
                title      = EXCLUDED.title,
                sort_order = EXCLUDED.sort_order,
                updated_at = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("boardId", boardId);
        upsert.Parameters.AddWithValue("title", title);
        upsert.Parameters.AddWithValue("sortOrder", sortOrder);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    private static async Task UpsertKanbanCardAsync(
        NpgsqlConnection local, NpgsqlConnection remote, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT column_id, title, note_id, sort_order, created_at, updated_at
              FROM kanban_cards WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        var (columnId, title, noteId, sortOrder, createdAt, updatedAt) = (
            r.GetGuid(0), r.GetString(1),
            r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2),
            r.GetInt32(3), r.GetDateTime(4), r.GetDateTime(5));
        await r.CloseAsync();

        await using var upsert = new NpgsqlCommand(
            @"INSERT INTO kanban_cards
                (id, column_id, title, note_id, sort_order, created_at, updated_at)
              VALUES
                (@id, @columnId, @title, @noteId, @sortOrder, @createdAt, @updatedAt)
              ON CONFLICT (id) DO UPDATE SET
                column_id  = EXCLUDED.column_id,
                title      = EXCLUDED.title,
                note_id    = EXCLUDED.note_id,
                sort_order = EXCLUDED.sort_order,
                updated_at = EXCLUDED.updated_at",
            remote);
        upsert.Parameters.AddWithValue("id", id);
        upsert.Parameters.AddWithValue("columnId", columnId);
        upsert.Parameters.AddWithValue("title", title);
        upsert.Parameters.AddWithValue("noteId", (object?)noteId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("sortOrder", sortOrder);
        upsert.Parameters.AddWithValue("createdAt", createdAt);
        upsert.Parameters.AddWithValue("updatedAt", updatedAt);
        await upsert.ExecuteNonQueryAsync();
    }

    // Attachments use PostgreSQL Large Objects whose OIDs are server-local, so we cannot
    // simply copy the lo_oid value across databases. Instead we:
    //   1. Read the bytes from the local LO using lo_get() (requires a transaction).
    //   2. Delete any existing remote row inside a transaction (trigger calls lo_unlink).
    //   3. Create a fresh LO on remote using lo_from_bytea() and insert the metadata row
    //      (same transaction, so it rolls back atomically if lo_from_bytea fails).
    // This guarantees the remote always holds a complete, self-consistent copy of the file.
    private static async Task UpsertAttachmentAsync(string localCs, string remoteCs, Guid id)
    {
        // Step 1 — read metadata and bytes from local (lo_get requires a transaction).
        byte[]   bytes;
        Guid     noteId;
        string   filename, mimeType;
        long     size;
        DateTime createdAt;

        await using (var conn = new NpgsqlConnection(localCs))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT note_id, filename, mime_type, size, created_at, lo_get(lo_oid) " +
                "FROM attachments WHERE id = @id",
                conn);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return; // deleted locally; a DELETE sync_log entry handles remote
            noteId    = r.GetGuid(0);
            filename  = r.GetString(1);
            mimeType  = r.GetString(2);
            size      = r.GetInt64(3);
            createdAt = r.GetDateTime(4);
            bytes     = r.GetFieldValue<byte[]>(5);
            await r.CloseAsync();
            await tx.CommitAsync();
        }

        // Step 2+3 — upload to remote in a single transaction so failure leaves nothing orphaned.
        // lo_from_bytea() requires an active transaction.
        await using (var conn = new NpgsqlConnection(remoteCs))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // Delete any existing row; trigger (trg_attachment_lo_cleanup) calls lo_unlink
            // on the old LO automatically, so we never leak large objects.
            await using (var del = new NpgsqlCommand(
                "DELETE FROM attachments WHERE id = @id", conn))
            {
                del.Parameters.AddWithValue("id", id);
                await del.ExecuteNonQueryAsync();
            }

            // Create a new large object from the raw bytes; returns its OID.
            uint newOid;
            await using (var loCmd = new NpgsqlCommand("SELECT lo_from_bytea(0, @data)", conn))
            {
                loCmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = bytes });
                await using var loReader = await loCmd.ExecuteReaderAsync();
                await loReader.ReadAsync();
                newOid = loReader.GetFieldValue<uint>(0);
            }

            // Insert the metadata row referencing the new LO.
            await using (var ins = new NpgsqlCommand(
                @"INSERT INTO attachments (id, note_id, lo_oid, filename, mime_type, size, created_at)
                  VALUES (@id, @noteId, @oid, @filename, @mimeType, @size, @createdAt)",
                conn))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("noteId", noteId);
                ins.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = newOid });
                ins.Parameters.AddWithValue("filename", filename);
                ins.Parameters.AddWithValue("mimeType", mimeType);
                ins.Parameters.AddWithValue("size", size);
                ins.Parameters.AddWithValue("createdAt", createdAt);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private static async Task PushDeleteAsync(
        NpgsqlConnection remote, string entityType, Guid id)
    {
        // Whitelist check before interpolating entityType into SQL.
        if (!KnownEntityTypes.Contains(entityType)) return;
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {entityType} WHERE id = @id", remote);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* row may already be gone on remote — safe to ignore */ }
    }

    private static async Task DeleteSyncLogEntriesAsync(
        NpgsqlConnection local, List<long> ids)
    {
        if (ids.Count == 0) return;
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM sync_log WHERE id = ANY(@ids)", local);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Pull ─────────────────────────────────────────────────────────────────

    // DTO for a note row fetched from the remote database.
    private record RemoteNote(
        Guid Id, Guid? ParentId, bool IsRoot, string Title, string Content, string ContentHash,
        Guid OwnerId, Guid CreatedBy, bool IsPrivate, int SortOrder,
        DateTime CreatedAt, DateTime UpdatedAt);

    private static async Task PullAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        // Process in FK dependency order so referenced rows exist before referencing ones.
        await PullUsersAsync(local, remote);
        await PullNotesAsync(local, remote, lastSyncedAt);
        await PullScratchpadsAsync(local, remote, lastSyncedAt);
        await PullKanbanBoardsAsync(local, remote, lastSyncedAt);
        await PullKanbanColumnsAsync(local, remote, lastSyncedAt);
        await PullKanbanCardsAsync(local, remote, lastSyncedAt);
        await PullRemoteDeletesAsync(local, remote, lastSyncedAt);
    }

    // Users have no updated_at column, so we pull all remote users and upsert locally.
    // Username changes on remote are applied; no conflict detection needed.
    private static async Task PullUsersAsync(NpgsqlConnection local, NpgsqlConnection remote)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, username, created_at FROM users", remote);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, string Username, DateTime CreatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetString(1), r.GetDateTime(2)));
        await r.CloseAsync();

        foreach (var (id, username, createdAt) in rows)
        {
            await using var upsert = new NpgsqlCommand(
                @"INSERT INTO users (id, username, created_at)
                  VALUES (@id, @username, @createdAt)
                  ON CONFLICT (id) DO UPDATE SET username = EXCLUDED.username",
                local);
            upsert.Parameters.AddWithValue("id", id);
            upsert.Parameters.AddWithValue("username", username);
            upsert.Parameters.AddWithValue("createdAt", createdAt);
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private static async Task PullNotesAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        // Fetch all notes modified on remote since the last sync.
        var remoteNotes = new List<RemoteNote>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT id, parent_id, is_root, title, content, content_hash,
                     owner_id, created_by, is_private, sort_order, created_at, updated_at
              FROM notes WHERE updated_at > @since",
            remote))
        {
            cmd.Parameters.AddWithValue("since", lastSyncedAt.UtcDateTime);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                remoteNotes.Add(new RemoteNote(
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetGuid(1),
                    r.GetBoolean(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetGuid(6), r.GetGuid(7), r.GetBoolean(8), r.GetInt32(9),
                    r.GetDateTime(10), r.GetDateTime(11)));
        }

        if (remoteNotes.Count == 0) return;

        // Pass 1: insert/update without parent_id to avoid FK ordering issues.
        // Track which notes need their parent_id fixed in pass 2.
        var parentIdFixes = new List<(Guid Id, Guid? ParentId)>();

        foreach (var rn in remoteNotes)
        {
            await using var checkCmd = new NpgsqlCommand(
                "SELECT updated_at FROM notes WHERE id = @id", local);
            checkCmd.Parameters.AddWithValue("id", rn.Id);
            var raw = await checkCmd.ExecuteScalarAsync();

            if (raw is null)
            {
                // Note is new from remote — insert it.
                await InsertNoteFromRemoteAsync(local, rn);
                parentIdFixes.Add((rn.Id, rn.ParentId));
            }
            else
            {
                var localUpdatedAt = (DateTime)raw;
                if (localUpdatedAt <= lastSyncedAt.UtcDateTime)
                {
                    // Local copy is unchanged since last sync — apply remote changes.
                    await UpdateNoteFromRemoteAsync(local, rn);
                    parentIdFixes.Add((rn.Id, rn.ParentId));
                }
                else
                {
                    // Both sides modified — keep local, create a [CONFLICT] sibling.
                    await CreateConflictNoteAsync(local, rn);
                }
            }
        }

        // Pass 2: restore correct parent_ids now that all notes are present locally.
        foreach (var (id, parentId) in parentIdFixes)
        {
            await using var fix = new NpgsqlCommand(
                "UPDATE notes SET parent_id = @pid WHERE id = @id", local);
            fix.Parameters.AddWithValue("pid", (object?)parentId ?? DBNull.Value);
            fix.Parameters.AddWithValue("id", id);
            await fix.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertNoteFromRemoteAsync(NpgsqlConnection local, RemoteNote rn)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at)
              VALUES
                (@id, NULL, @isRoot, @title, @content, @hash,
                 @ownerId, @createdBy, @isPrivate, @sortOrder, @createdAt, @updatedAt)
              ON CONFLICT (id) DO NOTHING",
            local);
        cmd.Parameters.AddWithValue("id", rn.Id);
        cmd.Parameters.AddWithValue("isRoot", rn.IsRoot);
        cmd.Parameters.AddWithValue("title", rn.Title);
        cmd.Parameters.AddWithValue("content", rn.Content);
        cmd.Parameters.AddWithValue("hash", rn.ContentHash);
        cmd.Parameters.AddWithValue("ownerId", rn.OwnerId);
        cmd.Parameters.AddWithValue("createdBy", rn.CreatedBy);
        cmd.Parameters.AddWithValue("isPrivate", rn.IsPrivate);
        cmd.Parameters.AddWithValue("sortOrder", rn.SortOrder);
        cmd.Parameters.AddWithValue("createdAt", rn.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", rn.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task UpdateNoteFromRemoteAsync(NpgsqlConnection local, RemoteNote rn)
    {
        // parent_id is intentionally excluded — handled by the fix pass in PullNotesAsync.
        // created_at and created_by are immutable and not updated.
        await using var cmd = new NpgsqlCommand(
            @"UPDATE notes SET
                is_root      = @isRoot,
                title        = @title,
                content      = @content,
                content_hash = @hash,
                owner_id     = @ownerId,
                is_private   = @isPrivate,
                sort_order   = @sortOrder,
                updated_at   = @updatedAt
              WHERE id = @id",
            local);
        cmd.Parameters.AddWithValue("id", rn.Id);
        cmd.Parameters.AddWithValue("isRoot", rn.IsRoot);
        cmd.Parameters.AddWithValue("title", rn.Title);
        cmd.Parameters.AddWithValue("content", rn.Content);
        cmd.Parameters.AddWithValue("hash", rn.ContentHash);
        cmd.Parameters.AddWithValue("ownerId", rn.OwnerId);
        cmd.Parameters.AddWithValue("isPrivate", rn.IsPrivate);
        cmd.Parameters.AddWithValue("sortOrder", rn.SortOrder);
        cmd.Parameters.AddWithValue("updatedAt", rn.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    // Both sides modified: keep the local version, insert the remote version as a
    // [CONFLICT] sibling so the user can manually reconcile.
    private static async Task CreateConflictNoteAsync(NpgsqlConnection local, RemoteNote rn)
    {
        // Read local note's parent_id and sort_order for sibling placement.
        await using var readCmd = new NpgsqlCommand(
            "SELECT parent_id, sort_order FROM notes WHERE id = @id", local);
        readCmd.Parameters.AddWithValue("id", rn.Id);
        await using var pr = await readCmd.ExecuteReaderAsync();
        if (!await pr.ReadAsync()) return;
        var parentId  = pr.IsDBNull(0) ? (Guid?)null : pr.GetGuid(0);
        var sortOrder = pr.GetInt32(1);
        await pr.CloseAsync();

        var username  = await GetUsernameAsync(local, rn.OwnerId);
        var timestamp = rn.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO notes
                (id, parent_id, is_root, title, content, content_hash,
                 owner_id, created_by, is_private, sort_order, created_at, updated_at)
              VALUES
                (gen_random_uuid(), @parentId, FALSE, @title, @content, @hash,
                 @ownerId, @ownerId, @isPrivate, @sortOrder, NOW(), NOW())",
            local);
        cmd.Parameters.AddWithValue("parentId", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("title",
            $"[CONFLICT] {rn.Title} (by {username} on {timestamp})");
        cmd.Parameters.AddWithValue("content", rn.Content);
        cmd.Parameters.AddWithValue("hash", rn.ContentHash);
        cmd.Parameters.AddWithValue("ownerId", rn.OwnerId);
        cmd.Parameters.AddWithValue("isPrivate", rn.IsPrivate);
        cmd.Parameters.AddWithValue("sortOrder", sortOrder + 1);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetUsernameAsync(NpgsqlConnection local, Guid userId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT username FROM users WHERE id = @id", local);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteScalarAsync() as string ?? "unknown";
    }

    // For non-note entities, conflict resolution is last-write-wins (remote wins).
    // These entities rarely conflict and don't have a natural "sibling" for a conflict copy.

    private static async Task PullScratchpadsAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, user_id, content, content_hash, updated_at FROM scratchpads WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", lastSyncedAt.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, Guid UserId, string Content, string Hash, DateTime UpdatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        await r.CloseAsync();

        foreach (var (id, userId, content, hash, updatedAt) in rows)
        {
            // Conflict: remote wins (scratchpads have no tree for a sibling conflict copy).
            await using var upsert = new NpgsqlCommand(
                @"INSERT INTO scratchpads (id, user_id, content, content_hash, updated_at)
                  VALUES (@id, @userId, @content, @hash, @updatedAt)
                  ON CONFLICT (user_id) DO UPDATE SET
                    content      = EXCLUDED.content,
                    content_hash = EXCLUDED.content_hash,
                    updated_at   = EXCLUDED.updated_at",
                local);
            upsert.Parameters.AddWithValue("id", id);
            upsert.Parameters.AddWithValue("userId", userId);
            upsert.Parameters.AddWithValue("content", content);
            upsert.Parameters.AddWithValue("hash", hash);
            upsert.Parameters.AddWithValue("updatedAt", updatedAt);
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private static async Task PullKanbanBoardsAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, owner_id, created_at, updated_at FROM kanban_boards WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", lastSyncedAt.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, string Title, Guid OwnerId, DateTime CreatedAt, DateTime UpdatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetDateTime(3), r.GetDateTime(4)));
        await r.CloseAsync();

        foreach (var (id, title, ownerId, createdAt, updatedAt) in rows)
        {
            await using var upsert = new NpgsqlCommand(
                @"INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at)
                  VALUES (@id, @title, @ownerId, @createdAt, @updatedAt)
                  ON CONFLICT (id) DO UPDATE SET
                    title      = EXCLUDED.title,
                    updated_at = EXCLUDED.updated_at",
                local);
            upsert.Parameters.AddWithValue("id", id);
            upsert.Parameters.AddWithValue("title", title);
            upsert.Parameters.AddWithValue("ownerId", ownerId);
            upsert.Parameters.AddWithValue("createdAt", createdAt);
            upsert.Parameters.AddWithValue("updatedAt", updatedAt);
            await upsert.ExecuteNonQueryAsync();
        }
    }

    private static async Task PullKanbanColumnsAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, board_id, title, sort_order, updated_at FROM kanban_columns WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", lastSyncedAt.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, Guid BoardId, string Title, int SortOrder, DateTime UpdatedAt)>();
        while (await r.ReadAsync())
            rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetDateTime(4)));
        await r.CloseAsync();

        foreach (var (id, boardId, title, sortOrder, updatedAt) in rows)
        {
            try
            {
                await using var upsert = new NpgsqlCommand(
                    @"INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at)
                      VALUES (@id, @boardId, @title, @sortOrder, @updatedAt)
                      ON CONFLICT (id) DO UPDATE SET
                        board_id   = EXCLUDED.board_id,
                        title      = EXCLUDED.title,
                        sort_order = EXCLUDED.sort_order,
                        updated_at = EXCLUDED.updated_at",
                    local);
                upsert.Parameters.AddWithValue("id", id);
                upsert.Parameters.AddWithValue("boardId", boardId);
                upsert.Parameters.AddWithValue("title", title);
                upsert.Parameters.AddWithValue("sortOrder", sortOrder);
                upsert.Parameters.AddWithValue("updatedAt", updatedAt);
                await upsert.ExecuteNonQueryAsync();
            }
            catch
            {
                // Parent board may not exist locally yet (e.g., not in this pull window).
                // Skip — the next sync cycle will resolve it when the board is pulled.
            }
        }
    }

    private static async Task PullKanbanCardsAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT id, column_id, title, note_id, sort_order, created_at, updated_at
              FROM kanban_cards WHERE updated_at > @since",
            remote);
        cmd.Parameters.AddWithValue("since", lastSyncedAt.UtcDateTime);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Id, Guid ColumnId, string Title, Guid? NoteId, int SortOrder, DateTime CreatedAt, DateTime UpdatedAt)>();
        while (await r.ReadAsync())
            rows.Add((
                r.GetGuid(0), r.GetGuid(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3),
                r.GetInt32(4), r.GetDateTime(5), r.GetDateTime(6)));
        await r.CloseAsync();

        foreach (var (id, columnId, title, noteId, sortOrder, createdAt, updatedAt) in rows)
        {
            try
            {
                await using var upsert = new NpgsqlCommand(
                    @"INSERT INTO kanban_cards
                        (id, column_id, title, note_id, sort_order, created_at, updated_at)
                      VALUES
                        (@id, @columnId, @title, @noteId, @sortOrder, @createdAt, @updatedAt)
                      ON CONFLICT (id) DO UPDATE SET
                        column_id  = EXCLUDED.column_id,
                        title      = EXCLUDED.title,
                        note_id    = EXCLUDED.note_id,
                        sort_order = EXCLUDED.sort_order,
                        updated_at = EXCLUDED.updated_at",
                    local);
                upsert.Parameters.AddWithValue("id", id);
                upsert.Parameters.AddWithValue("columnId", columnId);
                upsert.Parameters.AddWithValue("title", title);
                upsert.Parameters.AddWithValue("noteId", (object?)noteId ?? DBNull.Value);
                upsert.Parameters.AddWithValue("sortOrder", sortOrder);
                upsert.Parameters.AddWithValue("createdAt", createdAt);
                upsert.Parameters.AddWithValue("updatedAt", updatedAt);
                await upsert.ExecuteNonQueryAsync();
            }
            catch
            {
                // Parent column may not exist locally yet. Skip — next sync will resolve.
            }
        }
    }

    // ── Remote-delete detection ───────────────────────────────────────────────

    // Tables for which we run remote-delete detection. Users and scratchpads are
    // intentionally excluded: users accumulate and are never deleted in practice;
    // scratchpads use user_id (not id) as their natural key, making UUID comparison
    // unreliable. Kanban boards are processed first so their ON DELETE CASCADE
    // handles orphaned columns and cards on the local side automatically.
    private static readonly string[] DeleteDetectionOrder =
        ["kanban_boards", "kanban_columns", "kanban_cards", "notes"];

    private static async Task PullRemoteDeletesAsync(
        NpgsqlConnection local, NpgsqlConnection remote, DateTimeOffset lastSyncedAt)
    {
        // Collect all entity IDs that are pending push (still in sync_log).
        // These exist locally but haven't reached remote yet — they're local
        // creates/edits, not remote deletes, and must be preserved.
        var pendingIds = new HashSet<Guid>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT DISTINCT entity_id FROM sync_log", local))
        {
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                pendingIds.Add(r.GetGuid(0));
        }

        foreach (var table in DeleteDetectionOrder)
        {
            var localExtraWhere = table == "notes" ? "is_root = FALSE" : null;
            await ApplyRemoteDeletesForTableAsync(
                local, remote, table, lastSyncedAt, pendingIds, localExtraWhere);
        }
    }

    private static async Task ApplyRemoteDeletesForTableAsync(
        NpgsqlConnection local, NpgsqlConnection remote,
        string tableName, DateTimeOffset lastSyncedAt,
        HashSet<Guid> pendingPushIds,
        string? localExtraWhere = null)
    {
        // Safety: tableName comes from a known constant array, but validate anyway.
        if (!KnownEntityTypes.Contains(tableName)) return;

        // Fetch all IDs currently on remote.
        var remoteIds = new HashSet<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT id FROM {tableName}", remote))
        {
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                remoteIds.Add(r.GetGuid(0));
        }

        // Fetch local IDs and their updated_at.
        var localRows = new Dictionary<Guid, DateTime>();
        var whereClause = localExtraWhere is null ? "" : $" WHERE {localExtraWhere}";
        await using (var cmd = new NpgsqlCommand(
            $"SELECT id, updated_at FROM {tableName}{whereClause}", local))
        {
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                localRows[r.GetGuid(0)] = r.GetDateTime(1);
        }

        foreach (var (id, localUpdatedAt) in localRows)
        {
            if (remoteIds.Contains(id))    continue; // still exists on remote — not deleted
            if (pendingPushIds.Contains(id)) continue; // pending local push — not a remote delete

            if (localUpdatedAt <= lastSyncedAt.UtcDateTime)
            {
                // Unmodified locally since last sync → safe to delete.
                try
                {
                    await using var del = new NpgsqlCommand(
                        $"DELETE FROM {tableName} WHERE id = @id", local);
                    del.Parameters.AddWithValue("id", id);
                    await del.ExecuteNonQueryAsync();
                }
                catch { /* may already be gone via cascade — ignore */ }
            }
            // else: local edit wins — the sync_log UPDATE entry will push it next cycle.
        }
    }
}
