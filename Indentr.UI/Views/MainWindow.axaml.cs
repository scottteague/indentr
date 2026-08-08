using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Indentr.UI.Config;
using Microsoft.Data.Sqlite;

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
                RecoveryManager.WriteNote(_rootNote.Id, title, content);
                try
                {
                    // Root note privacy is not user-configurable; always public.
                    var result = await App.Notes.SaveAsync(_rootNote, hash, App.CurrentUser.Id);
                    if (result == SaveResult.Success)
                    {
                        RecoveryManager.Delete($"note-{_rootNote.Id}.json");
                        RootEditor.UpdateOriginalHash(_rootNote.ContentHash);
                    }
                    return result;
                }
                catch
                {
                    return SaveResult.Error;
                }
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

    private void OnTrashClicked(object? sender, RoutedEventArgs e) =>
        new TrashWindow().Show();

    private async void OnExportSingleNoteClicked(object? sender, RoutedEventArgs e) =>
        await RootEditor.ExportAsync();

    private async void OnExportSubtreeClicked(object? sender, RoutedEventArgs e) =>
        await RootEditor.ExportSubtreeAsync();

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select export folder to import"
        });
        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (path is null) return;

        try
        {
            var r = await SubtreeImporter.ImportAsync(App.Notes, App.Kanban, App.Attachments, path, App.CurrentUser.Id);
            await MessageBox.ShowInfo(this, "Import Complete",
                $"Imported {r.NotesImported} notes, {r.BoardsImported} boards, {r.AttachmentsImported} attachments.");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowError(this, "Import Failed", ex.Message);
        }
    }

private async void OnBackupClicked(object? sender, RoutedEventArgs e)
    {
        if (App.CurrentProfile.Backend == BackendType.SQLite)
            await BackupSqliteAsync();
        else
            await BackupPostgresAsync();
    }

    private async Task BackupSqliteAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save SQLite Backup",
            SuggestedFileName = $"indentr-backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.db",
            FileTypeChoices   = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }]
        });
        if (file is null) return;

        var destPath = file.TryGetLocalPath();
        if (destPath is null) return;

        var sourcePath = App.ResolveSqlitePath(App.CurrentProfile);
        try
        {
            // VACUUM INTO writes a clean, compacted copy without touching the live db.
            await using var conn = new SqliteConnection($"Data Source={sourcePath}");
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand($"VACUUM INTO '{destPath.Replace("'", "''")}'", conn);
            await cmd.ExecuteNonQueryAsync();

            await MessageBox.ShowInfo(this, "Backup Complete", $"Database backed up to:\n{destPath}");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowError(this, "Backup Failed", ex.Message);
        }
    }

    private async Task BackupPostgresAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Database Backup",
            SuggestedFileName = $"indentr-backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.sql",
            FileTypeChoices   = [new FilePickerFileType("SQL dump") { Patterns = ["*.sql"] }]
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        var db  = App.CurrentProfile.Database;
        var psi = new ProcessStartInfo("pg_dump")
        {
            UseShellExecute       = false,
            RedirectStandardError = true,
            CreateNoWindow        = true,
        };
        psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(db.Host);
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(db.Port.ToString());
        psi.ArgumentList.Add("-U"); psi.ArgumentList.Add(db.Username);
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(db.Name);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(path);
        if (!string.IsNullOrEmpty(db.Password))
            psi.Environment["PGPASSWORD"] = db.Password;

        try
        {
            using var proc   = Process.Start(psi)!;
            var       stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
                await MessageBox.ShowInfo(this, "Backup Complete", $"Database backed up to:\n{path}");
            else
                await MessageBox.ShowError(this, "Backup Failed",
                    string.IsNullOrWhiteSpace(stderr) ? "pg_dump exited with an error." : stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException)
        {
            await MessageBox.ShowError(this, "Backup Failed",
                "pg_dump was not found. Please install PostgreSQL client tools and ensure they are in your PATH.");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowError(this, "Backup Failed", ex.Message);
        }
    }

    private async void OnManageProfilesClicked(object? sender, RoutedEventArgs e)
    {
        var config   = ConfigManager.Load();
        var selected = await ProfilePickerWindow.ShowForManageAsync(config);
        if (selected is null) return; // picker closed without switching

        // Save the root note and all open note windows before restarting.
        await RootEditor.DoSave();
        await NotesWindow.CloseAllAsync();
        await KanbanWindow.CloseAllAsync();

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
        await NotesWindow.SaveAllAsync();
        await ScratchpadWindow.SaveAllAsync();
        if (App.CurrentProfile.RemoteDatabase is not null)
            await App.Sync.SyncOnceAsync();
        await KanbanWindow.CloseAllAsync();
        _closing = true;
        Close();
    }
}
