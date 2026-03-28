using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Indentr.Data.Repositories;
using Indentr.Data.Sqlite;
using Indentr.Data.Sqlite.Repositories;
using Indentr.UI.Config;
using Indentr.UI.Views;

namespace Indentr.UI;

public partial class App : Application
{
    // Shared services, set after profile selection and DB init
    public static INoteRepository       Notes       { get; private set; } = null!;
    public static IUserRepository       Users       { get; private set; } = null!;
    public static IScratchpadRepository Scratchpads { get; private set; } = null!;
    public static IAttachmentStore      Attachments { get; private set; } = null!;
    public static IKanbanRepository     Kanban      { get; private set; } = null!;
    public static ISyncService          Sync        { get; private set; } = null!;
    public static User                  CurrentUser { get; private set; } = null!;
    public static DatabaseProfile       CurrentProfile { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new LoadingWindow();
            _ = StartupAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var config = ConfigManager.Load();

        DatabaseProfile? profile;

        if (config.Profiles.Count == 1)
        {
            // Exactly one profile — use it directly, no picker needed.
            profile             = config.Profiles[0];
            config.LastProfile  = profile.Name;
            ConfigManager.Save(config);
        }
        else
        {
            // 0 profiles (first run) or 2+ profiles — show the picker.
            desktop.MainWindow!.Hide();
            profile = await ProfilePickerWindow.ShowForStartupAsync(config);
            if (profile is null)
            {
                desktop.Shutdown();
                return;
            }
            desktop.MainWindow.Show();
        }

        CurrentProfile = profile;

        string? remoteCs = null;
        if (profile.RemoteDatabase is { } remote)
            remoteCs = ConnectionStringBuilder.Build(
                remote.Host, remote.Port, remote.Name, remote.Username, remote.Password);

        if (profile.Backend == BackendType.SQLite)
        {
            // ── SQLite local backend ──────────────────────────────────────────
            var dbPath = ResolveSqlitePath(profile);

            try
            {
                await new SqliteDatabaseMigrator(dbPath).MigrateAsync();
            }
            catch (Exception ex)
            {
                await MessageBox.ShowError(desktop.MainWindow!,
                    "Database Error",
                    $"Could not open or migrate the SQLite database:\n\n{ex.Message}\n\nPath: {dbPath}");
                desktop.Shutdown();
                return;
            }

            Notes       = new SqliteNoteRepository(dbPath);
            Users       = new SqliteUserRepository(dbPath);
            Scratchpads = new SqliteScratchpadRepository(dbPath);
            Attachments = new SqliteAttachmentStore(dbPath);
            Kanban      = new SqliteKanbanRepository(dbPath);

            CurrentUser = await Users.GetOrCreateAsync(profile.Username);
            Sync        = new SqliteSyncService(dbPath, remoteCs, CurrentUser.Id);
        }
        else
        {
            // ── PostgreSQL local backend ──────────────────────────────────────
            var schemaName = string.IsNullOrEmpty(profile.LocalSchemaId)
                ? null
                : $"indentr_{profile.LocalSchemaId}";

            var cs = ConnectionStringBuilder.Build(
                profile.Database.Host, profile.Database.Port,
                profile.Database.Name, profile.Database.Username, profile.Database.Password,
                schemaName);

            Notes       = new NoteRepository(cs);
            Users       = new UserRepository(cs);
            Scratchpads = new ScratchpadRepository(cs);
            Attachments = new PostgresAttachmentStore(cs);
            Kanban      = new KanbanRepository(cs);

            // Migrate schema.
            try
            {
                await new DatabaseMigrator(cs).MigrateAsync(schemaName);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowError(desktop.MainWindow!,
                    "Database Error",
                    $"Could not connect or migrate the database:\n\n{ex.Message}\n\nPlease check your config at ~/.config/indentr/config.json");
                desktop.Shutdown();
                return;
            }

            CurrentUser = await Users.GetOrCreateAsync(profile.Username);
            Sync        = new SyncService(cs, remoteCs, CurrentUser.Id);
        }

        var recoveries = RecoveryManager.Scan();
        if (recoveries.Count > 0)
            await RecoveryWindow.ShowAsync(desktop.MainWindow!, recoveries);

        await Notes.EnsureRootExistsAsync(CurrentUser.Id);
        await Scratchpads.GetOrCreateForUserAsync(CurrentUser.Id);

        // Open main window.
        var loadingWindow = desktop.MainWindow;
        var mainWindow    = new MainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        loadingWindow?.Close();
    }

    /// <summary>Returns the resolved absolute path for a SQLite profile's database file.
    /// Explicit paths are used as-is; blank paths default to ~/.config/indentr/&lt;schemaId&gt;.db.</summary>
    internal static string ResolveSqlitePath(DatabaseProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.SqliteDbPath))
            return profile.SqliteDbPath;

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "indentr");
        Directory.CreateDirectory(configDir);

        var fileName = string.IsNullOrEmpty(profile.LocalSchemaId)
            ? $"{profile.Name.ToLowerInvariant()}.db"
            : $"{profile.LocalSchemaId}.db";

        return Path.Combine(configDir, fileName);
    }
}
