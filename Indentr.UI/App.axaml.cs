using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Indentr.Data.Repositories;
using Indentr.UI.Config;
using Indentr.UI.Views;

namespace Indentr.UI;

public partial class App : Application
{
    // Shared services, set after profile selection and DB init
    public static INoteRepository     Notes          { get; private set; } = null!;
    public static IUserRepository     Users          { get; private set; } = null!;
    public static IScratchpadRepository Scratchpads  { get; private set; } = null!;
    public static IAttachmentStore    Attachments    { get; private set; } = null!;
    public static IKanbanRepository   Kanban         { get; private set; } = null!;
    public static ISyncService        Sync           { get; private set; } = null!;
    public static User                CurrentUser    { get; private set; } = null!;
    public static DatabaseProfile     CurrentProfile { get; private set; } = null!;

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
            profile = await ProfilePickerWindow.ShowForStartupAsync(config);
            if (profile is null)
            {
                desktop.Shutdown();
                return;
            }
        }

        CurrentProfile = profile;

        // Build local connection string, scoped to this profile's schema.
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

        var remoteCs = profile.RemoteDatabase is { } rdb
            ? ConnectionStringBuilder.Build(rdb.Host, rdb.Port, rdb.Name, rdb.Username, rdb.Password)
            : null;

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

        // Ensure user and bootstrap data exist.
        // If the remote already knows this username, adopt its UUID so both machines share
        // the same identity — otherwise a fresh local DB would generate a different UUID,
        // causing username unique-constraint collisions during push/pull and the privacy
        // filter (created_by = @userId) silently excluding the other machine's notes.
        Guid? remoteUserId = null;
        if (remoteCs is not null)
        {
            try
            {
                // Migrate the remote schema first so the users table is guaranteed to exist
                // even if sync has never run against this remote before.
                await new DatabaseMigrator(remoteCs).MigrateAsync();
                remoteUserId = (await new UserRepository(remoteCs).GetByUsernameAsync(profile.Username))?.Id;
            }
            catch { /* Remote unavailable or migration failed — proceed with a local UUID. */ }
        }

        CurrentUser = remoteUserId.HasValue
            ? await Users.GetOrCreateWithIdAsync(remoteUserId.Value, profile.Username)
            : await Users.GetOrCreateAsync(profile.Username);

        // SyncService is created here so that when we've just adopted a remote identity
        // (new machine joining an existing account) we can run an initial pull before
        // EnsureRootExistsAsync. Without this, EnsureRootExistsAsync creates a fresh local
        // root note that conflicts with the remote's existing root on the first push.
        Sync = new SyncService(cs, remoteCs, CurrentUser.Id);

        if (remoteUserId.HasValue)
            await Sync.SyncOnceAsync(); // pull existing root/scratchpad; ignore failures

        await Notes.EnsureRootExistsAsync(CurrentUser.Id);
        await Scratchpads.GetOrCreateForUserAsync(CurrentUser.Id);

        // Open main window.
        var loadingWindow = desktop.MainWindow;
        var mainWindow    = new MainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        loadingWindow?.Close();
    }
}
