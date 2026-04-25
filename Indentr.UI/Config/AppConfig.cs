namespace Indentr.UI.Config;

public enum BackendType { PostgreSQL, SQLite }

public class DatabaseProfile
{
    public string Name           { get; set; } = "";
    public string Username       { get; set; } = "";
    public BackendType Backend   { get; set; } = BackendType.PostgreSQL;
    /// <summary>Path to the SQLite database file. Used when Backend == SQLite.</summary>
    public string SqliteDbPath   { get; set; } = "";
    public DatabaseConfig  Database       { get; set; } = new();
    // Optional remote database for sync. Null = sync disabled for this profile.
    public DatabaseConfig? RemoteDatabase { get; set; }
}

public class AppConfig
{
    public string LastProfile { get; set; } = "";
    public List<DatabaseProfile> Profiles { get; set; } = new();
    public double EditorFontSize { get; set; } = 14.0;
}

public class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "indentr";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
}
