using Microsoft.Data.Sqlite;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Sqlite.Repositories;

public class SqliteUserRepository(string dbPath) : IUserRepository
{
    private SqliteConnection Open() => SqliteHelper.Open(dbPath);

    public async Task<User?> GetByUsernameAsync(string username)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, username, created_at FROM users WHERE username = @username", conn);
        cmd.Parameters.AddWithValue("@username", username);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<User> GetOrCreateAsync(string username)
    {
        await using var conn = Open();
        await conn.OpenAsync();

        // Try to find existing first (SQLite upsert with RETURNING needs care).
        await using var find = new SqliteCommand(
            "SELECT id, username, created_at FROM users WHERE username = @username", conn);
        find.Parameters.AddWithValue("@username", username);
        await using var fr = await find.ExecuteReaderAsync();
        if (await fr.ReadAsync()) return Map(fr);
        await fr.CloseAsync();

        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var ins = new SqliteCommand(
            "INSERT OR IGNORE INTO users (id, username, created_at) VALUES (@id, @username, @now)", conn);
        ins.Parameters.AddWithValue("@id",       id);
        ins.Parameters.AddWithValue("@username", username);
        ins.Parameters.AddWithValue("@now",      now);
        await ins.ExecuteNonQueryAsync();

        // Re-read in case a concurrent insert won the race.
        await using var get = new SqliteCommand(
            "SELECT id, username, created_at FROM users WHERE username = @username", conn);
        get.Parameters.AddWithValue("@username", username);
        await using var gr = await get.ExecuteReaderAsync();
        await gr.ReadAsync();
        return Map(gr);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, username, created_at FROM users ORDER BY username", conn);
        var users = new List<User>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            users.Add(Map(r));
        return users;
    }

    private static User Map(SqliteDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        Username  = r.GetString(1),
        CreatedAt = r.GetDateTime(2)
    };
}
