using Microsoft.Data.Sqlite;

namespace Indentr.Data.Sqlite;

public class SqliteDatabaseMigrator(string dbPath)
{
    public async Task MigrateAsync()
    {
        // Ensure the directory exists.
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // WAL mode: better concurrent read performance.
        await using (var pragma = new SqliteCommand("PRAGMA journal_mode=WAL", conn))
            await pragma.ExecuteNonQueryAsync();

        // Enable foreign key enforcement (SQLite ignores FKs by default).
        await using (var fk = new SqliteCommand("PRAGMA foreign_keys=ON", conn))
            await fk.ExecuteNonQueryAsync();

        var currentVersion = await GetCurrentVersionAsync(conn);

        foreach (var (version, sql) in GetMigrations().Where(m => m.Version > currentVersion).OrderBy(m => m.Version))
        {
            // Execute each migration's SQL statements separated by semicolons.
            // We run them as a single batch; SQLite handles multiple statements.
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection conn)
    {
        try
        {
            await using var cmd = new SqliteCommand(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations", conn);
            var result = await cmd.ExecuteScalarAsync();
            return result is long v ? (int)v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<(int Version, string Sql)> GetMigrations()
    {
        yield return (1, ReadResource("Indentr.Data.Sqlite.Migrations.001_InitialSchema.sql"));
        yield return (2, ReadResource("Indentr.Data.Sqlite.Migrations.002_PrivacyAndPerUserRoot.sql"));
        yield return (3, ReadResource("Indentr.Data.Sqlite.Migrations.003_Attachments.sql"));
        yield return (4, ReadResource("Indentr.Data.Sqlite.Migrations.004_Kanban.sql"));
        yield return (5, ReadResource("Indentr.Data.Sqlite.Migrations.005_SyncLog.sql"));
        yield return (6, ReadResource("Indentr.Data.Sqlite.Migrations.006_SoftDelete.sql"));
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(SqliteDatabaseMigrator).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
