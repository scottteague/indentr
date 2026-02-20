namespace Organiz.Data;

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
}
