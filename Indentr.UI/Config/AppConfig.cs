namespace Indentr.UI.Config;

public class DatabaseProfile
{
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public DatabaseConfig Database { get; set; } = new();
}

public class AppConfig
{
    public string LastProfile { get; set; } = "";
    public List<DatabaseProfile> Profiles { get; set; } = new();
}

public class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "indentr";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
}
