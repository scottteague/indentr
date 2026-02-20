using Avalonia.Controls;
using Avalonia.Interactivity;
using Organiz.Core.Interfaces;
using Organiz.Core.Models;

namespace Organiz.UI.Views;

public partial class MainWindow : Window
{
    // ── Static accessor so other windows can save/reload the root note ────────

    private static MainWindow? _instance;

    public static async Task SaveIfRootAsync(Guid noteId)
    {
        if (_instance?._rootNote?.Id == noteId)
            await _instance.RootEditor.DoSave();
    }

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

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

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
        _closing = true;
        Close();
    }
}
