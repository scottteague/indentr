using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;
using Indentr.Data;
using Indentr.Data.Sqlite;

namespace Indentr.Tests;

/// <summary>
/// Integration tests for the pull-side timestamp guard in SqliteSyncService.
///
/// Regression for: the pull phase unconditionally overwrote locally-modified kanban
/// cards, discarding position changes and note links made between push and pull.
///
/// Strategy: seed remote with stale card data and local with the newer version.
/// Clear local sync_log so the push phase sends nothing (remote stays stale).
/// After SyncOnceAsync, assert whether the guard correctly preserved or applied
/// the remote data depending on the relationship of local.updated_at to lastSyncedAt.
/// </summary>
public class SqliteKanbanSyncGuardTests : IAsyncLifetime
{
    // One PostgreSQL container is shared across all tests in the class.
    private PostgreSqlContainer _pg = null!;

    // The same user UUID and username is inserted in both remote PG and each local
    // SQLite so that BuildUserIdRemapAsync produces an empty remap (no UUID translation).
    private readonly Guid   _userId   = Guid.NewGuid();
    private readonly string _username = "syncguard_" + Guid.NewGuid().ToString("N")[..8];

    // ── xUnit lifecycle ───────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _pg.StartAsync();
        await new DatabaseMigrator(_pg.GetConnectionString()).MigrateAsync();

        await using var conn = await OpenRemoteAsync();
        await PgExec(conn,
            "INSERT INTO users (id, username, created_at) VALUES (@id, @u, @t)",
            ("id", (object)_userId), ("u", _username), ("t", DateTime.UtcNow));
    }

    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_DoesNotOverwriteLocalCard_WhenLocalWasModifiedAfterLastSync()
    {
        // Arrange
        //   Remote: card at sort_order=0, no note link.
        //   Local:  card at sort_order=1, linked to a note, updated 2 min ago.
        //   Last sync: 5 min ago  →  local.updated_at > lastSyncedAt  →  guard blocks pull.
        // Expected: local card is unchanged after sync.

        var localDb      = await CreateLocalDbAsync();
        var boardId      = Guid.NewGuid();
        var colId        = Guid.NewGuid();
        var cardId       = Guid.NewGuid();
        var noteId       = Guid.NewGuid();
        var lastSynced   = DateTime.UtcNow.AddMinutes(-5);
        var localUpdated = DateTime.UtcNow.AddMinutes(-2); // newer than lastSynced

        try
        {
            await SeedRemote(boardId, colId, cardId, sortOrder: 0, noteId: null);
            await SeedLocalNote(localDb, noteId); // must exist before card references it via FK
            await SeedLocal(localDb, boardId, colId, cardId, sortOrder: 1, noteId: noteId, localUpdated);
            await SetLocalLastSynced(localDb, lastSynced);

            await new SqliteSyncService(localDb, _pg.GetConnectionString(), _userId)
                .SyncOnceAsync();

            var (sortOrder, actualNoteId) = await ReadLocalCard(localDb, cardId);
            Assert.Equal(1, sortOrder);         // remote's 0 must not have overwritten local's 1
            Assert.Equal(noteId, actualNoteId); // note link must be preserved
        }
        finally { TryDeleteFile(localDb); }
    }

    [Fact]
    public async Task Pull_AppliesRemoteCard_WhenLocalWasNotModifiedSinceLastSync()
    {
        // Arrange
        //   Remote: card at sort_order=99.
        //   Local:  card at sort_order=1, last updated 5 min ago.
        //   Last sync: 1 min ago  →  local.updated_at < lastSyncedAt  →  guard allows pull.
        // Expected: local card takes the remote value after sync.

        var localDb      = await CreateLocalDbAsync();
        var boardId      = Guid.NewGuid();
        var colId        = Guid.NewGuid();
        var cardId       = Guid.NewGuid();
        var lastSynced   = DateTime.UtcNow.AddMinutes(-1);
        var localUpdated = DateTime.UtcNow.AddMinutes(-5); // older than lastSynced

        try
        {
            await SeedRemote(boardId, colId, cardId, sortOrder: 99, noteId: null);
            await SeedLocal(localDb, boardId, colId, cardId, sortOrder: 1, noteId: null, localUpdated);
            await SetLocalLastSynced(localDb, lastSynced);

            await new SqliteSyncService(localDb, _pg.GetConnectionString(), _userId)
                .SyncOnceAsync();

            var (sortOrder, _) = await ReadLocalCard(localDb, cardId);
            Assert.Equal(99, sortOrder); // remote's 99 must have been applied
        }
        finally { TryDeleteFile(localDb); }
    }

    // ── seed helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a board → column → card into the remote PostgreSQL.
    /// Board and column use updated_at one hour ago so they fall outside the pull's
    /// safetyFilterSince window and don't interfere with the card assertion.
    /// The card uses the column default (NOW()) so it is inside the window.
    /// </summary>
    private async Task SeedRemote(Guid boardId, Guid colId, Guid cardId, int sortOrder, Guid? noteId)
    {
        await using var c = await OpenRemoteAsync();
        await PgExec(c,
            "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
            "VALUES (@id, 'B', @uid, NOW()-INTERVAL '1 hour', NOW()-INTERVAL '1 hour')",
            ("id", (object)boardId), ("uid", _userId));
        await PgExec(c,
            "INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at) " +
            "VALUES (@id, @bid, 'C', 0, NOW()-INTERVAL '1 hour')",
            ("id", (object)colId), ("bid", boardId));
        // No explicit updated_at: the column default (NOW()) is used, keeping it in the pull window.
        await PgExec(c,
            "INSERT INTO kanban_cards (id, column_id, title, note_id, sort_order, created_at) " +
            "VALUES (@id, @cid, 'Card', @nid, @so, NOW())",
            ("id",  (object)cardId),
            ("cid", colId),
            ("nid", (object?)noteId ?? DBNull.Value),
            ("so",  sortOrder));
    }

    /// <summary>
    /// Inserts a board → column → card into local SQLite with the specified
    /// sort_order, noteId, and card updated_at.  Clears sync_log afterwards so
    /// the push phase sends nothing and the remote card remains at its seeded value.
    /// </summary>
    private async Task SeedLocal(
        string dbPath, Guid boardId, Guid colId, Guid cardId,
        int sortOrder, Guid? noteId, DateTime localUpdated)
    {
        await using var c = OpenLocal(dbPath);
        await c.OpenAsync();

        var old = Iso(DateTime.UtcNow.AddHours(-1));
        await LiteExec(c,
            "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
            "VALUES (@id, 'B', @uid, @t, @t)",
            ("@id", boardId.ToString()), ("@uid", _userId.ToString()), ("@t", old));
        await LiteExec(c,
            "INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at) " +
            "VALUES (@id, @bid, 'C', 0, @t)",
            ("@id", colId.ToString()), ("@bid", boardId.ToString()), ("@t", old));
        await LiteExec(c,
            "INSERT INTO kanban_cards " +
            "(id, column_id, title, note_id, sort_order, created_at, updated_at) " +
            "VALUES (@id, @cid, 'Card', @nid, @so, @old, @ua)",
            ("@id",  cardId.ToString()),
            ("@cid", colId.ToString()),
            ("@nid", noteId.HasValue ? (object)noteId.Value.ToString() : DBNull.Value),
            ("@so",  sortOrder),
            ("@old", old),
            ("@ua",  Iso(localUpdated)));

        // Clear only kanban sync_log entries so the push phase sends nothing for these
        // entities and the remote card stays at its seeded stale value.
        // Non-kanban entries (e.g. a local note) are left intact so that:
        //   a) they get pushed to remote (making them safe from remote-delete detection), and
        //   b) pendingIds in PullRemoteDeletesAsync protects them until the push confirms them.
        await LiteExec(c,
            "DELETE FROM sync_log WHERE entity_type LIKE 'kanban%'");
    }

    /// <summary>
    /// Inserts a minimal note locally so the card's note_id FK is satisfied.
    /// The sync_log entry is intentionally left so the note is pushed to remote during
    /// SyncOnceAsync, which keeps it safe from ApplyRemoteDeletesForTableAsync.
    /// </summary>
    private async Task SeedLocalNote(string dbPath, Guid noteId)
    {
        await using var c = OpenLocal(dbPath);
        await c.OpenAsync();
        var now = Iso(DateTime.UtcNow);
        await LiteExec(c,
            "INSERT INTO notes " +
            "(id, parent_id, is_root, title, content, content_hash, " +
            " owner_id, created_by, is_private, sort_order, created_at, updated_at) " +
            "VALUES (@id, NULL, 0, 'N', '', '', @uid, @uid, 0, 0, @t, @t)",
            ("@id", noteId.ToString()), ("@uid", _userId.ToString()), ("@t", now));
    }

    private static async Task SetLocalLastSynced(string dbPath, DateTime lastSynced)
    {
        await using var c = OpenLocal(dbPath);
        await c.OpenAsync();
        await LiteExec(c,
            "UPDATE sync_state SET last_synced_at = @t WHERE id = 1",
            ("@t", Iso(lastSynced)));
    }

    private static async Task<(int SortOrder, Guid? NoteId)> ReadLocalCard(string dbPath, Guid cardId)
    {
        await using var c = OpenLocal(dbPath);
        await c.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT sort_order, note_id FROM kanban_cards WHERE id = @id", c);
        cmd.Parameters.AddWithValue("@id", cardId.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "card not found in local SQLite after sync");
        return (r.GetInt32(0), r.IsDBNull(1) ? (Guid?)null : Guid.Parse(r.GetString(1)));
    }

    // ── fixture helpers ───────────────────────────────────────────────────────

    /// <summary>Creates a fresh migrated SQLite file with the test user pre-inserted.</summary>
    private async Task<string> CreateLocalDbAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"indentr_syncguard_{Guid.NewGuid():N}.db");
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        await using var c = OpenLocal(path);
        await c.OpenAsync();
        await LiteExec(c,
            "INSERT INTO users (id, username, created_at) VALUES (@id, @u, @t)",
            ("@id", _userId.ToString()), ("@u", _username), ("@t", Iso(DateTime.UtcNow)));
        return path;
    }

    private async Task<NpgsqlConnection> OpenRemoteAsync()
    {
        var c = new NpgsqlConnection(_pg.GetConnectionString());
        await c.OpenAsync();
        return c;
    }

    private static SqliteConnection OpenLocal(string path) =>
        new($"Data Source={path};Foreign Keys=True");

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ── SQL micro-helpers ─────────────────────────────────────────────────────

    private static async Task LiteExec(
        SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new SqliteCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task PgExec(
        NpgsqlConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("O");
}
