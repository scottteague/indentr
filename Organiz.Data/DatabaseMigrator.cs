using Npgsql;

namespace Organiz.Data;

public class DatabaseMigrator(string connectionString)
{
    public async Task MigrateAsync()
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        int currentVersion = await GetCurrentVersionAsync(conn);

        foreach (var (version, sql) in GetMigrations().Where(m => m.Version > currentVersion).OrderBy(m => m.Version))
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> GetCurrentVersionAsync(NpgsqlConnection conn)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations", conn);
            var result = await cmd.ExecuteScalarAsync();
            return result is DBNull or null ? 0 : Convert.ToInt32(result);
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<(int Version, string Sql)> GetMigrations()
    {
        yield return (1, ReadResource("Organiz.Data.Migrations.001_InitialSchema.sql"));
        yield return (2, ReadResource("Organiz.Data.Migrations.002_PrivacyAndPerUserRoot.sql"));
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
