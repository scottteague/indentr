using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Organiz.Core.Interfaces;
using Organiz.Core.Models;

namespace Organiz.Data.Repositories;

public class ScratchpadRepository(string connectionString) : IScratchpadRepository
{
    public async Task<Scratchpad> GetOrCreateForUserAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO scratchpads (user_id, content, content_hash)
              VALUES (@userId, '', '')
              ON CONFLICT (user_id) DO UPDATE SET user_id = EXCLUDED.user_id
              RETURNING id, user_id, content, content_hash, updated_at", conn);
        cmd.Parameters.AddWithValue("userId", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return MapScratchpad(reader);
    }

    public async Task<SaveResult> SaveAsync(Scratchpad scratchpad, string originalHash)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var checkCmd = new NpgsqlCommand(
            "SELECT content_hash FROM scratchpads WHERE user_id = @userId", conn);
        checkCmd.Parameters.AddWithValue("userId", scratchpad.UserId);
        var storedHash = (string?)await checkCmd.ExecuteScalarAsync() ?? "";

        if (storedHash != originalHash && originalHash != "")
            return SaveResult.Conflict;

        var newHash = ComputeHash(scratchpad.Content);
        await using var saveCmd = new NpgsqlCommand(
            @"UPDATE scratchpads SET content = @content, content_hash = @hash, updated_at = NOW()
              WHERE user_id = @userId", conn);
        saveCmd.Parameters.AddWithValue("content", scratchpad.Content);
        saveCmd.Parameters.AddWithValue("hash", newHash);
        saveCmd.Parameters.AddWithValue("userId", scratchpad.UserId);
        await saveCmd.ExecuteNonQueryAsync();

        scratchpad.ContentHash = newHash;
        return SaveResult.Success;
    }

    private static Scratchpad MapScratchpad(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        UserId = r.GetGuid(1),
        Content = r.GetString(2),
        ContentHash = r.GetString(3),
        UpdatedAt = r.GetDateTime(4)
    };

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
