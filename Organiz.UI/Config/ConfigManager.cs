using System.Text.Json;

namespace Organiz.UI.Config;

public static class ConfigManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "organiz");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

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
                    var profile = new DatabaseProfile
                    {
                        Name     = "Default",
                        Username = legacy.Username,
                        Database = legacy.Database
                    };
                    config.Profiles.Add(profile);
                    config.LastProfile = profile.Name;
                    Save(config);
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
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public static bool IsFirstRun() => !File.Exists(ConfigPath) || Load().Profiles.Count == 0;

    // Used only to detect the pre-profile config format during migration.
    private sealed class LegacyAppConfig
    {
        public string Username { get; set; } = "";
        public DatabaseConfig Database { get; set; } = new();
    }
}
