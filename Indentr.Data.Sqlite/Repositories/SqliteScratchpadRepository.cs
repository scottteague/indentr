using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Sqlite.Repositories;

public class SqliteScratchpadRepository(string dbPath) : IScratchpadRepository
{
    public async Task<Scratchpad> GetOrCreateForUserAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        var uid = userId.ToString();

        // Try insert; if already exists the INSERT OR IGNORE is a no-op.
        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var ins = new SqliteCommand(
            "INSERT OR IGNORE INTO scratchpads (id, user_id, content, content_hash, updated_at) VALUES (@id, @uid, '', '', @now)",
            conn);
        ins.Parameters.AddWithValue("@id",  id);
        ins.Parameters.AddWithValue("@uid", uid);
        ins.Parameters.AddWithValue("@now", now);
        await ins.ExecuteNonQueryAsync();

        await using var sel = new SqliteCommand(
            "SELECT id, user_id, content, content_hash, updated_at FROM scratchpads WHERE user_id = @uid",
            conn);
        sel.Parameters.AddWithValue("@uid", uid);
        await using var r = await sel.ExecuteReaderAsync();
        await r.ReadAsync();
        return Map(r);
    }

    public async Task<SaveResult> SaveAsync(Scratchpad scratchpad, string originalHash)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();

        await using var check = new SqliteCommand(
            "SELECT content_hash FROM scratchpads WHERE user_id = @uid", conn);
        check.Parameters.AddWithValue("@uid", scratchpad.UserId.ToString());
        var storedHash = (string?)await check.ExecuteScalarAsync() ?? "";

        if (storedHash != originalHash && originalHash != "")
            return SaveResult.Conflict;

        var newHash = ComputeHash(scratchpad.Content);
        await using var save = new SqliteCommand(
            "UPDATE scratchpads SET content = @content, content_hash = @hash, updated_at = @now WHERE user_id = @uid",
            conn);
        save.Parameters.AddWithValue("@content", scratchpad.Content);
        save.Parameters.AddWithValue("@hash",    newHash);
        save.Parameters.AddWithValue("@now",     SqliteHelper.UtcNow());
        save.Parameters.AddWithValue("@uid",     scratchpad.UserId.ToString());
        await save.ExecuteNonQueryAsync();

        scratchpad.ContentHash = newHash;
        return SaveResult.Success;
    }

    private static Scratchpad Map(SqliteDataReader r) => new()
    {
        Id          = r.GetGuid(0),
        UserId      = r.GetGuid(1),
        Content     = r.GetString(2),
        ContentHash = r.GetString(3),
        UpdatedAt   = r.GetDateTime(4)
    };

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
