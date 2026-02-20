namespace Organiz.UI.Config;

public class AppConfig
{
    public string Username { get; set; } = "";
    public DatabaseConfig Database { get; set; } = new();
}

public class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "organiz";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
}
