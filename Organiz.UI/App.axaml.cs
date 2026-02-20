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
    // Shared services, set after first-run / DB init
    public static INoteRepository Notes { get; private set; } = null!;
    public static IUserRepository Users { get; private set; } = null!;
    public static IScratchpadRepository Scratchpads { get; private set; } = null!;
    public static User CurrentUser { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Run startup asynchronously but block the first window open
            desktop.MainWindow = new LoadingWindow();
            _ = StartupAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var config = ConfigManager.Load();

        // First run: prompt for username and DB settings
        if (ConfigManager.IsFirstRun())
        {
            var firstRun = new FirstRunWindow(config);
            var ok = await firstRun.ShowDialogAsync();
            if (!ok)
            {
                desktop.Shutdown();
                return;
            }
            ConfigManager.Save(config);
        }

        // Build connection string and wire up repositories
        var cs = ConnectionStringBuilder.Build(
            config.Database.Host, config.Database.Port,
            config.Database.Name, config.Database.Username, config.Database.Password);

        Notes = new NoteRepository(cs);
        Users = new UserRepository(cs);
        Scratchpads = new ScratchpadRepository(cs);

        // Migrate schema
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

        // Ensure user exists
        CurrentUser = await Users.GetOrCreateAsync(config.Username);

        // Bootstrap root note and scratchpad
        await Notes.EnsureRootExistsAsync(CurrentUser.Id);
        await Scratchpads.GetOrCreateForUserAsync(CurrentUser.Id);

        // Open main window — save the loading window ref before replacing it
        var loadingWindow = desktop.MainWindow;
        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        loadingWindow?.Close();
    }
}
