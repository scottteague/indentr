using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        _instance?.SaveAllAsync() ?? Task.CompletedTask;

    public static async Task ReloadIfRootAsync(Guid noteId)
    {
        if (_instance?._rootNote?.Id != noteId) return;
        var fresh = await App.Notes.GetByIdAsync(noteId);
        if (fresh is null) return;
        _instance._rootNote = fresh;
        _instance.RootEditor.RefreshNote(fresh);
    }

    // ── Instance ─────────────────────────────────────────────────────────────

    private Note? _rootNote;
    private bool  _closing;

    public MainWindow()
    {
        _instance = this;
        Closed += (_, _) => _instance = null;
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
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
                    var result = await App.Notes.SaveAsync(_rootNote, hash);
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

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await SaveAllAsync();
        }
    }

    // Saves all open editing surfaces. Also the target of TriggerSyncSaveAsync() called from child windows.
    private async Task SaveAllAsync()
    {
        await RootEditor.DoSave();
        await NotesWindow.SaveAllAsync();
        await ScratchpadWindow.SaveAllAsync();
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
        await RootEditor.DoSave();
        await NotesWindow.SaveAllAsync();
        await ScratchpadWindow.SaveAllAsync();
        await KanbanWindow.CloseAllAsync();
        _closing = true;
        Close();
    }
}
