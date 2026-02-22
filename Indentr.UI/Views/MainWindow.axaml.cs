using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.UI.Config;

namespace Indentr.UI.Views;

public partial class MainWindow : Window
{
    // ── Static accessor so other windows can save/reload the root note ────────

    private static MainWindow? _instance;

    public static async Task SaveIfRootAsync(Guid noteId)
    {
        if (_instance?._rootNote?.Id == noteId)
            await _instance.RootEditor.DoSave();
    }

    // Called by NotesWindow and ScratchpadWindow when Shift+Ctrl+S is pressed there.
    public static Task TriggerSyncSaveAsync() =>
        _instance?.SaveAllAndSyncAsync() ?? Task.CompletedTask;

    public static async Task ReloadIfRootAsync(Guid noteId)
    {
        if (_instance?._rootNote?.Id != noteId) return;
        var fresh = await App.Notes.GetByIdAsync(noteId);
        if (fresh is null) return;
        _instance._rootNote = fresh;
        _instance.RootEditor.RefreshNote(fresh);
    }

    // ── Instance ─────────────────────────────────────────────────────────────

    private Note?            _rootNote;
    private bool             _closing;
    private DispatcherTimer? _syncTimer;

    public MainWindow()
    {
        _instance = this;
        Closed += (_, _) => _instance = null;
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Show the sync bar and start the background timer only when a remote is configured.
        if (App.CurrentProfile.RemoteDatabase is not null)
        {
            SyncBar.IsVisible = true;
            var lastSync = await App.Sync.GetLastSyncedAtAsync();
            SyncStatusText.Text = lastSync == DateTimeOffset.MinValue
                ? "Never synced"
                : $"Last synced at {lastSync.ToLocalTime():HH:mm}";

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _syncTimer.Tick += async (_, _) => await RunSyncAsync();
            _syncTimer.Start();
        }

        _rootNote = await App.Notes.GetRootAsync(App.CurrentUser.Id);
        if (_rootNote is not null)
        {
            RootEditor.LoadNote(_rootNote, App.CurrentUser.Id);

            RootEditor.SaveRequested += async (title, content, hash, _) =>
            {
                _rootNote.Title   = title;
                _rootNote.Content = content;
                _rootNote.OwnerId = App.CurrentUser.Id;
                // Root note privacy is not user-configurable; always public.
                var result = await App.Notes.SaveAsync(_rootNote, hash);
                if (result == SaveResult.Success)
                    RootEditor.UpdateOriginalHash(_rootNote.ContentHash);
                return result;
            };

            RootEditor.InAppLinkClicked += async id =>
            {
                await RootEditor.DoSave();
                await NotesWindow.OpenAsync(id);
            };
            RootEditor.ExternalLinkClicked   += OpenBrowser;
            RootEditor.NewChildNoteRequested += CreateChildNote;
        }
    }

    private async Task<Note?> CreateChildNote(string title, Guid parentId)
    {
        var child = await App.Notes.CreateAsync(new Note
        {
            ParentId  = parentId,
            Title     = title,
            Content   = "",
            OwnerId   = App.CurrentUser.Id,
            CreatedBy = App.CurrentUser.Id,
            SortOrder = 0
        });
        await NotesWindow.OpenAsync(child.Id);
        return child;
    }

    private static void OpenBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // ── Menu handlers ────────────────────────────────────────────────────────

    private void OnSearchClicked(object? sender, RoutedEventArgs e) =>
        new SearchWindow().Show();

    private async void OnScratchpadClicked(object? sender, RoutedEventArgs e) =>
        await ScratchpadWindow.OpenAsync();

    private async void OnManageClicked(object? sender, RoutedEventArgs e)
    {
        await RootEditor.DoSave();
        new ManagementWindow().Show();
    }

    private async void OnManageProfilesClicked(object? sender, RoutedEventArgs e)
    {
        var config   = ConfigManager.Load();
        var selected = await ProfilePickerWindow.ShowForManageAsync(config);
        if (selected is null) return; // picker closed without switching

        // Save the root note and all open note windows before restarting.
        await RootEditor.DoSave();
        await NotesWindow.CloseAllAsync();

        // Restart the process; the current instance exits via Close().
        var exe = Environment.ProcessPath;
        if (exe is not null)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });

        _closing = true; // skip the save-on-close in OnClosing
        Close();
    }

    private async void OnSyncNowClicked(object? sender, RoutedEventArgs e) => await RunSyncAsync();

    // Shared by the manual button and the auto-sync timer. Guards against concurrent
    // syncs by checking whether the button is already disabled.
    private async Task RunSyncAsync()
    {
        if (!SyncNowButton.IsEnabled) return; // already syncing
        SyncNowButton.IsEnabled = false;
        SyncStatusText.Text     = "Syncing…";

        var result = await App.Sync.SyncOnceAsync();

        SyncStatusText.Text = result.Status switch
        {
            SyncStatus.Success => $"Synced at {DateTime.Now:HH:mm}",
            SyncStatus.Offline => "Offline",
            SyncStatus.Failed  => $"Sync failed: {result.Message}",
            _                  => "Unknown sync state"
        };

        SyncNowButton.IsEnabled = true;
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await SaveAllAndSyncAsync();
        }
    }

    // Saves all open editing surfaces, then runs a sync cycle if a remote is configured.
    // Also the target of TriggerSyncSaveAsync() called from child windows.
    private async Task SaveAllAndSyncAsync()
    {
        await RootEditor.DoSave();
        await NotesWindow.SaveAllAsync();
        await ScratchpadWindow.SaveAllAsync();
        if (App.CurrentProfile.RemoteDatabase is not null)
            await RunSyncAsync();
    }

    // ── Close: cancel → save → re-close ─────────────────────────────────────
    // async void OnClosing doesn't work: base.OnClosing runs before the awaits
    // complete, destroying the window. Instead we cancel the first close,
    // save, then close again with the _closing flag set.

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _ = SaveAndCloseAsync();
        }
        base.OnClosing(e);
    }

    private async Task SaveAndCloseAsync()
    {
        _syncTimer?.Stop();
        await RootEditor.DoSave();
        _closing = true;
        Close();
    }
}
