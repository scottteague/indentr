using Avalonia.Controls;
using Avalonia.Interactivity;
using Indentr.Data;
using Indentr.UI.Config;

namespace Indentr.UI.Views;

public partial class ProfilePickerWindow : Window
{
    private readonly AppConfig _config;
    private readonly bool _isStartupMode;
    private DatabaseProfile? _result;
    private readonly Dictionary<string, string?> _syncLines = new();

    private record ProfileListItem(string DisplayName, string? SyncLine)
    {
        public bool HasSyncLine => SyncLine is not null;
    }

    // ── Static factory methods ────────────────────────────────────────────────

    /// <summary>Shows the picker at startup. Returns the chosen profile, or null if the
    /// user cancelled (caller should shut down the app).</summary>
    public static Task<DatabaseProfile?> ShowForStartupAsync(AppConfig config)
    {
        var win = new ProfilePickerWindow(config, isStartupMode: true);
        var tcs = new TaskCompletionSource<DatabaseProfile?>();
        win.Closed += (_, _) => tcs.TrySetResult(win._result);
        win.Show();
        return tcs.Task;
    }

    /// <summary>Shows the picker from the main menu. Returns the chosen profile if the
    /// user wants to switch, or null if they just closed it without switching.</summary>
    public static Task<DatabaseProfile?> ShowForManageAsync(AppConfig config)
    {
        var win = new ProfilePickerWindow(config, isStartupMode: false);
        var tcs = new TaskCompletionSource<DatabaseProfile?>();
        win.Closed += (_, _) => tcs.TrySetResult(win._result);
        win.Show();
        return tcs.Task;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    private ProfilePickerWindow(AppConfig config, bool isStartupMode)
    {
        _config = config;
        _isStartupMode = isStartupMode;
        InitializeComponent();

        if (!isStartupMode)
        {
            Title                = "Manage Profiles";
            ActionButton.Content = "Switch & Restart";
        }

        RefreshList();
        _ = LoadSyncLinesAsync();

        // First-ever run: no profiles yet — open the Add dialog immediately.
        if (isStartupMode && config.Profiles.Count == 0)
            _ = OpenAddDialogAsync();
    }

    // ── Sync info loading ─────────────────────────────────────────────────────

    // Queries each profile's local sync_state and re-renders the list once done.
    // Runs fire-and-forget from the constructor; local DB reads are fast so the
    // sync lines appear almost immediately after the window opens.
    private async Task LoadSyncLinesAsync()
    {
        foreach (var profile in _config.Profiles)
        {
            if (profile.RemoteDatabase is null) continue;
            _syncLines[profile.Name] = await ReadSyncLineAsync(profile);
        }
        RefreshList();
    }

    private static async Task<string?> ReadSyncLineAsync(DatabaseProfile profile)
    {
        try
        {
            var cs  = ConnectionStringBuilder.Build(
                profile.Database.Host, profile.Database.Port,
                profile.Database.Name, profile.Database.Username, profile.Database.Password);
            var svc = new SyncService(cs, remoteConnectionString: null);
            var ts  = await svc.GetLastSyncedAtAsync();
            if (ts == DateTimeOffset.MinValue) return "Never synced";
            var local = ts.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? $"Synced today at {local:HH:mm}"
                : $"Synced {local:d MMM} at {local:HH:mm}";
        }
        catch
        {
            return null; // DB unreachable — omit sync line rather than showing an error
        }
    }

    // ── List management ───────────────────────────────────────────────────────

    private void RefreshList()
    {
        // Preserve the current selection across refreshes (e.g. after async sync-line load).
        var selectedName = ProfileList.SelectedIndex >= 0
            ? _config.Profiles[ProfileList.SelectedIndex].Name
            : null;

        var items = _config.Profiles.Select(p =>
        {
            var displayName = !_isStartupMode && p.Name == App.CurrentProfile?.Name
                ? $"{p.Name}  ✓"
                : p.Name;
            _syncLines.TryGetValue(p.Name, out var syncLine);
            return new ProfileListItem(displayName, syncLine);
        }).ToList();

        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = items;

        var restoreName = selectedName ?? _config.LastProfile;
        var idx = _config.Profiles.FindIndex(p => p.Name == restoreName);
        ProfileList.SelectedIndex = idx >= 0 ? idx : (_config.Profiles.Count > 0 ? 0 : -1);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var has = ProfileList.SelectedIndex >= 0;
        EditButton.IsEnabled   = has;
        DeleteButton.IsEnabled = has;
        ActionButton.IsEnabled = has;
    }

    // ── Add / Edit / Delete ───────────────────────────────────────────────────

    private async Task OpenAddDialogAsync()
    {
        var profile = new DatabaseProfile();
        var win = new FirstRunWindow(profile, isNew: true);
        var ok = await win.ShowDialogAsync(this);

        if (!ok)
        {
            // First-run cancellation with no profiles → abort entirely.
            if (_isStartupMode && _config.Profiles.Count == 0)
                Close();
            return;
        }

        if (_config.Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            await MessageBox.ShowError(this, "Duplicate Name", $"A profile named \"{profile.Name}\" already exists.");
            return;
        }

        _config.Profiles.Add(profile);
        _config.LastProfile = profile.Name;
        ConfigManager.Save(_config);
        RefreshList();
    }

    private async void OnAddClicked(object? sender, RoutedEventArgs e) => await OpenAddDialogAsync();

    private async void OnEditClicked(object? sender, RoutedEventArgs e)
    {
        var idx = ProfileList.SelectedIndex;
        if (idx < 0) return;

        var profile      = _config.Profiles[idx];
        var originalName = profile.Name;

        var win = new FirstRunWindow(profile, isNew: false);
        var ok  = await win.ShowDialogAsync(this);
        if (!ok) return;

        // If the name changed, check for duplicates.
        if (!profile.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase) &&
            _config.Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            profile.Name = originalName; // revert
            await MessageBox.ShowError(this, "Duplicate Name", $"A profile named \"{profile.Name}\" already exists.");
            return;
        }

        if (_config.LastProfile == originalName)
            _config.LastProfile = profile.Name;

        ConfigManager.Save(_config);
        RefreshList();
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        var idx = ProfileList.SelectedIndex;
        if (idx < 0) return;

        var profile   = _config.Profiles[idx];
        var confirmed = await MessageBox.ShowConfirm(this, "Delete Profile", $"Delete profile \"{profile.Name}\"?");
        if (!confirmed) return;

        _config.Profiles.RemoveAt(idx);
        if (_config.LastProfile == profile.Name)
            _config.LastProfile = _config.Profiles.FirstOrDefault()?.Name ?? "";

        ConfigManager.Save(_config);
        RefreshList();
    }

    // ── Open / Switch ─────────────────────────────────────────────────────────

    private void OnActionClicked(object? sender, RoutedEventArgs e)
    {
        var idx = ProfileList.SelectedIndex;
        if (idx < 0) return;

        _result             = _config.Profiles[idx];
        _config.LastProfile = _result.Name;
        ConfigManager.Save(_config);
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();
}
