using System.Text.Json;

namespace Indentr.Web.Config;

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "indentr", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

            // Migrate from pre-profile format (Username + Database at top level).
            if (config.Profiles.Count == 0)
            {
                var legacy = JsonSerializer.Deserialize<LegacyAppConfig>(json, JsonOptions);
                if (legacy != null && !string.IsNullOrWhiteSpace(legacy.Username))
                {
                    config.Profiles.Add(new DatabaseProfile
                    {
                        Name     = "Default",
                        Username = legacy.Username,
                        Database = legacy.Database
                    });
                }
            }

            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private sealed class LegacyAppConfig
    {
        public string        Username { get; set; } = "";
        public DatabaseConfig Database { get; set; } = new();
    }
}
