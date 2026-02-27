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

    private static User MapUser(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Username = r.GetString(1),
        CreatedAt = r.GetDateTime(2)
    };
}
