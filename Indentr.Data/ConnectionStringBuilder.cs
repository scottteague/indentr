namespace Indentr.Data;

public static class ConnectionStringBuilder
{
    public static string Build(string host, int port, string database, string username, string password)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            Pooling = true,
            MaxPoolSize = 10
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Attempts to open and immediately close a connection to the given connection string.
    /// Returns null on success, or an error message string on failure.
    /// Uses Pooling=false so test connections don't pollute the pool.
    /// </summary>
    public static async Task<string?> TryConnectAsync(string connectionString, int timeoutSeconds = 5)
    {
        try
        {
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            await using var conn = new Npgsql.NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cts.Token);
            return null; // success
        }
        catch (OperationCanceledException)
        {
            return $"Connection timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
