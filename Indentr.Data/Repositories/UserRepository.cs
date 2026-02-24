using Npgsql;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Repositories;

public class UserRepository(string connectionString) : IUserRepository
{
    public async Task<User?> GetByUsernameAsync(string username)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, username, created_at FROM users WHERE username = @username", conn);
        cmd.Parameters.AddWithValue("username", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapUser(reader) : null;
    }

    public async Task<User> GetOrCreateAsync(string username)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO users (username) VALUES (@username)
              ON CONFLICT (username) DO UPDATE SET username = EXCLUDED.username
              RETURNING id, username, created_at", conn);
        cmd.Parameters.AddWithValue("username", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return MapUser(reader);
    }

    // Used on startup when a remote already has this username: adopt the remote UUID so
    // both machines share the same identity. Safe on a fresh local DB (no FK-dependent
    // rows yet); if FK rows exist the PK update will fail with a constraint error rather
    // than silently diverging.
    public async Task<User> GetOrCreateWithIdAsync(Guid id, string username)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO users (id, username) VALUES (@id, @username)
              ON CONFLICT (username) DO UPDATE SET id = EXCLUDED.id
              RETURNING id, username, created_at", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("username", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return MapUser(reader);
    }

    private static User MapUser(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Username = r.GetString(1),
        CreatedAt = r.GetDateTime(2)
    };
}
