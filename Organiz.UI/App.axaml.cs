using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Organiz.Core.Interfaces;
using Organiz.Core.Models;
using Organiz.Data;
using Organiz.Data.Repositories;
using Organiz.UI.Config;
using Organiz.UI.Views;

namespace Organiz.UI;

public partial class App : Application
{
    // Shared services, set after profile selection and DB init
    public static INoteRepository     Notes          { get; private set; } = null!;
    public static IUserRepository     Users          { get; private set; } = null!;
    public static IScratchpadRepository Scratchpads  { get; private set; } = null!;
    public static IAttachmentStore    Attachments    { get; private set; } = null!;
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

        // Build connection string and wire up repositories.
        var cs = ConnectionStringBuilder.Build(
            profile.Database.Host, profile.Database.Port,
            profile.Database.Name, profile.Database.Username, profile.Database.Password);

        Notes       = new NoteRepository(cs);
        Users       = new UserRepository(cs);
        Scratchpads = new ScratchpadRepository(cs);
        Attachments = new PostgresAttachmentStore(cs);

        // Migrate schema.
        try
        {
            await new DatabaseMigrator(cs).MigrateAsync();
        }
        catch (Exception ex)
        {
            await MessageBox.ShowError(desktop.MainWindow!,
                "Database Error",
                $"Could not connect or migrate the database:\n\n{ex.Message}\n\nPlease check your config at ~/.config/organiz/config.json");
            desktop.Shutdown();
            return;
        }

        // Ensure user and bootstrap data exist.
        CurrentUser = await Users.GetOrCreateAsync(profile.Username);
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
