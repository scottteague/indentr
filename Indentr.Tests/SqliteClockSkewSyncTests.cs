using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;
using Indentr.Data;
using Indentr.Data.Sqlite;

namespace Indentr.Tests;

/// <summary>
/// Regression tests for the clock-skew sync bug:
///
/// When Machine A's local clock is behind the remote PostgreSQL clock, pushes from A
/// write an updated_at to remote that is earlier than Machine B's pull watermark
/// (last_synced_at - PullSafetyBuffer). Machine B's pull filter then silently misses
/// the change.
///
/// Notes are already immune because PushNoteAsync stamps updated_at = NOW() on remote.
/// These tests confirm that kanban boards and cards exhibit the bug and will pass once
/// the fix (using NOW() on push for those entity types) is applied.
///
/// Test topology:
///   machineA SQLite  ──push──►  remote PostgreSQL  ◄──pull──  machineB SQLite
///
/// Clock simulation: rows are inserted into Machine A's SQLite with an explicit
/// backdated updated_at (e.g. 5 minutes ago) to mimic a machine whose clock is
/// behind the remote PostgreSQL server. Machine B's last_synced_at is set to 2
/// minutes ago — meaning its pull filter window starts at 2.5 minutes ago — well
/// after Machine A's "old" timestamp. With the bug, the push preserves the old
/// timestamp on remote and Machine B misses it.
/// </summary>
public class SqliteClockSkewSyncTests : IAsyncLifetime
{
    private PostgreSqlContainer _pg = null!;

    // Both machines share the same username/UUID so BuildUserIdRemapAsync
    // produces an empty remap and doesn't interfere with the clock-skew logic.
    private readonly Guid   _userId   = Guid.NewGuid();
    private readonly string _username = "clockskew_" + Guid.NewGuid().ToString("N")[..8];

    // How far behind Machine A's clock is relative to the remote PostgreSQL server.
    // Chosen to be greater than PullSafetyBuffer (30 s) so the bug is triggered.
    private static readonly TimeSpan ClockBehindBy = TimeSpan.FromMinutes(5);

    // Machine B last synced 2 minutes ago (remote clock).
    // Pull filter window = last_synced_at - 30 s = 2.5 min ago.
    // Machine A's updated_at = 5 min ago → falls outside the window → bug fires.
    private static readonly TimeSpan MachineB_LastSyncedAgo = TimeSpan.FromMinutes(2);

    // ── xUnit lifecycle ───────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _pg.StartAsync();
        await new DatabaseMigrator(_pg.GetConnectionString()).MigrateAsync();

        // Seed the shared user into remote so push/pull don't trip on FK constraints.
        await using var conn = await OpenRemoteAsync();
        await PgExec(conn,
            "INSERT INTO users (id, username, created_at) VALUES (@id, @u, NOW())",
            ("id", (object)_userId), ("u", _username));
    }

    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Machine A edits a kanban board whose updated_at reflects a clock that is
    /// 5 minutes behind the remote server. Machine B last synced 2 minutes ago.
    /// After A pushes and B pulls, B should have the board.
    ///
    /// With the bug: push preserves updated_at = 5 min ago on remote.
    ///               B's filter (updated_at > 2.5 min ago) misses it.
    /// After fix:    push stamps updated_at = NOW() on remote.
    ///               B's filter catches it.
    /// </summary>
    [Fact]
    public async Task KanbanBoard_PushedByBehindClock_IsVisibleToPullingMachine()
    {
        var machineADb = await CreateLocalDbAsync();
        var machineBDb = await CreateLocalDbAsync();

        var boardId = Guid.NewGuid();

        // Machine A's "local time" when it last edited the board.
        var machineAUpdatedAt = DateTime.UtcNow - ClockBehindBy;

        // Machine B's last successful sync timestamp (remote-clock domain).
        var machineBLastSynced = DateTime.UtcNow - MachineB_LastSyncedAgo;

        try
        {
            // ── Seed Machine A ────────────────────────────────────────────────
            await using (var c = OpenLocal(machineADb))
            {
                await c.OpenAsync();
                // Insert a root note so the FK on notes is satisfied if needed,
                // and seed the board with the backdated timestamp.
                await LiteExec(c,
                    "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
                    "VALUES (@id, 'Shared Board', @uid, @ts, @ts)",
                    ("@id",  boardId.ToString()),
                    ("@uid", _userId.ToString()),
                    ("@ts",  Iso(machineAUpdatedAt)));
                // sync_log is populated by triggers; Machine A will push this on sync.
            }

            // ── Machine A syncs (push) ────────────────────────────────────────
            var syncA = new SqliteSyncService(machineADb, _pg.GetConnectionString(), _userId);
            await syncA.SyncOnceAsync();

            // Verify the board made it to remote (push sanity check).
            await using (var conn = await OpenRemoteAsync())
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT updated_at FROM kanban_boards WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("id", boardId);
                var remoteUpdatedAt = await cmd.ExecuteScalarAsync();
                Assert.True(remoteUpdatedAt is not null,
                    "Board was not pushed to remote at all — push sanity check failed.");
            }

            // ── Set Machine B's watermark to simulate a recent previous sync ─
            await SetLocalLastSynced(machineBDb, machineBLastSynced);

            // ── Machine B syncs (pull) ────────────────────────────────────────
            var syncB = new SqliteSyncService(machineBDb, _pg.GetConnectionString(), _userId);
            await syncB.SyncOnceAsync();

            // ── Assert ────────────────────────────────────────────────────────
            await using (var c = OpenLocal(machineBDb))
            {
                await c.OpenAsync();
                await using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM kanban_boards WHERE id = @id", c);
                cmd.Parameters.AddWithValue("@id", boardId.ToString());
                var count = (long)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal(1, count); // FAILS with bug, passes after fix
            }
        }
        finally
        {
            TryDeleteFile(machineADb);
            TryDeleteFile(machineBDb);
        }
    }

    /// <summary>
    /// Same scenario for a kanban card: Machine A edits a card with a backdated
    /// updated_at. After A pushes and B pulls, B should see the updated card title.
    /// </summary>
    [Fact]
    public async Task KanbanCard_PushedByBehindClock_IsVisibleToPullingMachine()
    {
        var machineADb = await CreateLocalDbAsync();
        var machineBDb = await CreateLocalDbAsync();

        var boardId  = Guid.NewGuid();
        var colId    = Guid.NewGuid();
        var cardId   = Guid.NewGuid();

        var machineAUpdatedAt  = DateTime.UtcNow - ClockBehindBy;
        var machineBLastSynced = DateTime.UtcNow - MachineB_LastSyncedAgo;

        try
        {
            // ── Seed board + column + card on remote so Machine B has the parent rows ─
            // (If B doesn't have the board/column, the card upsert will fail on FK.)
            await using (var conn = await OpenRemoteAsync())
            {
                await PgExec(conn,
                    "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
                    "VALUES (@id, 'B', @uid, NOW()-'1 hour'::interval, NOW()-'1 hour'::interval)",
                    ("id", (object)boardId), ("uid", _userId));
                await PgExec(conn,
                    "INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at) " +
                    "VALUES (@id, @bid, 'C', 0, NOW()-'1 hour'::interval)",
                    ("id", (object)colId), ("bid", boardId));
                // Original card title before Machine A's "edit".
                await PgExec(conn,
                    "INSERT INTO kanban_cards (id, column_id, title, sort_order, created_at, updated_at) " +
                    "VALUES (@id, @cid, 'Original Title', 0, NOW()-'2 hour'::interval, NOW()-'2 hour'::interval)",
                    ("id", (object)cardId), ("cid", colId));
            }

            // ── Seed Machine A with the board/col/card as if previously synced, ─
            //    then simulate a local edit (new title, backdated updated_at).
            await using (var c = OpenLocal(machineADb))
            {
                await c.OpenAsync();
                var oldTs = Iso(DateTime.UtcNow.AddHours(-2));
                await LiteExec(c,
                    "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
                    "VALUES (@id, 'B', @uid, @t, @t)",
                    ("@id", boardId.ToString()), ("@uid", _userId.ToString()), ("@t", oldTs));
                await LiteExec(c,
                    "INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at) " +
                    "VALUES (@id, @bid, 'C', 0, @t)",
                    ("@id", colId.ToString()), ("@bid", boardId.ToString()), ("@t", oldTs));
                await LiteExec(c,
                    "INSERT INTO kanban_cards (id, column_id, title, sort_order, created_at, updated_at) " +
                    "VALUES (@id, @cid, 'Edited Title', 0, @old, @edited)",
                    ("@id",     cardId.ToString()),
                    ("@cid",    colId.ToString()),
                    ("@old",    oldTs),
                    ("@edited", Iso(machineAUpdatedAt)));
                // sync_log will have entries for the above inserts; clear board/col
                // entries so only the card is pushed (keeps the test focused).
                await LiteExec(c,
                    "DELETE FROM sync_log WHERE entity_type IN ('kanban_boards','kanban_columns')");
            }

            // ── Machine A syncs (push card to remote) ─────────────────────────
            var syncA = new SqliteSyncService(machineADb, _pg.GetConnectionString(), _userId);
            await syncA.SyncOnceAsync();

            // Verify the card update made it to remote.
            await using (var conn = await OpenRemoteAsync())
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT title FROM kanban_cards WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("id", cardId);
                var title = (string?)await cmd.ExecuteScalarAsync();
                Assert.Equal("Edited Title", title);
            }

            // ── Seed Machine B with the old card (as if previously synced before A's edit) ─
            await using (var c = OpenLocal(machineBDb))
            {
                await c.OpenAsync();
                var oldTs = Iso(DateTime.UtcNow.AddHours(-2));
                await LiteExec(c,
                    "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) " +
                    "VALUES (@id, 'B', @uid, @t, @t)",
                    ("@id", boardId.ToString()), ("@uid", _userId.ToString()), ("@t", oldTs));
                await LiteExec(c,
                    "INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at) " +
                    "VALUES (@id, @bid, 'C', 0, @t)",
                    ("@id", colId.ToString()), ("@bid", boardId.ToString()), ("@t", oldTs));
                await LiteExec(c,
                    "INSERT INTO kanban_cards (id, column_id, title, sort_order, created_at, updated_at) " +
                    "VALUES (@id, @cid, 'Original Title', 0, @t, @t)",
                    ("@id", cardId.ToString()), ("@cid", colId.ToString()), ("@t", oldTs));
                // Clear sync_log so Machine B doesn't push anything back.
                await LiteExec(c, "DELETE FROM sync_log");
            }

            await SetLocalLastSynced(machineBDb, machineBLastSynced);

            // ── Machine B syncs (pull) ────────────────────────────────────────
            var syncB = new SqliteSyncService(machineBDb, _pg.GetConnectionString(), _userId);
            await syncB.SyncOnceAsync();

            // ── Assert ────────────────────────────────────────────────────────
            await using (var c = OpenLocal(machineBDb))
            {
                await c.OpenAsync();
                await using var cmd = new SqliteCommand(
                    "SELECT title FROM kanban_cards WHERE id = @id", c);
                cmd.Parameters.AddWithValue("@id", cardId.ToString());
                var title = (string?)await cmd.ExecuteScalarAsync();
                Assert.Equal("Edited Title", title); // FAILS with bug, passes after fix
            }
        }
        finally
        {
            TryDeleteFile(machineADb);
            TryDeleteFile(machineBDb);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> CreateLocalDbAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"indentr_clockskew_{Guid.NewGuid():N}.db");
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        await using var c = OpenLocal(path);
        await c.OpenAsync();
        await LiteExec(c,
            "INSERT INTO users (id, username, created_at) VALUES (@id, @u, @t)",
            ("@id", _userId.ToString()), ("@u", _username), ("@t", Iso(DateTime.UtcNow)));
        return path;
    }

    private static async Task SetLocalLastSynced(string dbPath, DateTime ts)
    {
        await using var c = OpenLocal(dbPath);
        await c.OpenAsync();
        await LiteExec(c,
            "UPDATE sync_state SET last_synced_at = @t WHERE id = 1",
            ("@t", Iso(ts)));
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
