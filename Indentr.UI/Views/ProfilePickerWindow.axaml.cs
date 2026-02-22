using Avalonia.Controls;
using Avalonia.Interactivity;
using Indentr.UI.Config;

namespace Indentr.UI.Views;

public partial class ProfilePickerWindow : Window
{
    private readonly AppConfig _config;
    private readonly bool _isStartupMode;
    private DatabaseProfile? _result;

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

        // First-ever run: no profiles yet — open the Add dialog immediately.
        if (isStartupMode && config.Profiles.Count == 0)
            _ = OpenAddDialogAsync();
    }

    // ── List management ───────────────────────────────────────────────────────

    private void RefreshList()
    {
        var items = _config.Profiles
            .Select(p => !_isStartupMode && p.Name == App.CurrentProfile?.Name
                ? $"{p.Name}  ✓"
                : p.Name)
            .ToList();

        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = items;

        var lastIdx = _config.Profiles.FindIndex(p => p.Name == _config.LastProfile);
        ProfileList.SelectedIndex = lastIdx >= 0 ? lastIdx : (_config.Profiles.Count > 0 ? 0 : -1);
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
