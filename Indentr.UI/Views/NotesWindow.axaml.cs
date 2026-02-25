using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class NotesWindow : Window
{
    // ── Static registry so other windows can save/reload open instances ───────

    private static readonly List<NotesWindow> _openWindows = new();

    public static async Task SaveIfOpenAsync(Guid noteId)
    {
        foreach (var win in _openWindows.Where(w => w._note.Id == noteId).ToList())
            await win.Editor.DoSave();
    }

    public static async Task ReloadIfOpenAsync(Guid noteId)
    {
        var matches = _openWindows.Where(w => w._note.Id == noteId).ToList();
        if (matches.Count == 0) return;
        var fresh = await App.Notes.GetByIdAsync(noteId);
        if (fresh is null) return;
        foreach (var win in matches)
        {
            win._note = fresh;
            win.Editor.RefreshNote(fresh);
            win.Title = fresh.Title.Length > 0 ? fresh.Title : "Untitled Note";
        }
    }

    public static async Task SaveAllAsync()
    {
        foreach (var win in _openWindows.ToList())
            await win.Editor.DoSave();
    }

    public static async Task CloseAllAsync()
    {
        foreach (var win in _openWindows.ToList())
            await win.SaveAndCloseAsync();
    }

    // ── Instance ─────────────────────────────────────────────────────────────

    private Note _note = null!;
    private bool _deleted;
    private bool _closing;

    public NotesWindow() => InitializeComponent();

    public static async Task OpenAsync(Guid noteId)
    {
        // If already open, bring that window to the front instead of opening a duplicate.
        var existing = _openWindows.FirstOrDefault(w => w._note.Id == noteId);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var note = await App.Notes.GetByIdAsync(noteId);
        if (note is null) return;

        // Hard privacy: block access to another user's private note.
        if (note.IsPrivate && note.CreatedBy != App.CurrentUser.Id)
        {
            await MessageBox.ShowError(null,
                "Access Denied",
                $"\"{note.Title}\" is a private note belonging to another user.");
            return;
        }

        var win = new NotesWindow();
        _openWindows.Add(win);
        win.Closed += (_, _) => _openWindows.Remove(win);
        win.LoadNote(note);
        win.Show();
    }

    private void LoadNote(Note note)
    {
        _note = note;
        Title = note.Title.Length > 0 ? note.Title : "Untitled Note";

        Editor.LoadNote(note, App.CurrentUser.Id);

        Editor.SaveRequested += async (title, content, originalHash, isPublic) =>
        {
            _note.Title     = title;
            _note.Content   = content;
            _note.OwnerId   = App.CurrentUser.Id;
            _note.IsPrivate = !isPublic;
            var result = await App.Notes.SaveAsync(_note, originalHash);
            if (result == SaveResult.Success || result == SaveResult.Conflict)
            {
                Editor.UpdateOriginalHash(_note.ContentHash);
                Title = _note.Title.Length > 0 ? _note.Title : "Untitled Note";
            }
            return result;
        };

        Editor.InAppLinkClicked += async id =>
        {
            await Editor.DoSave();
            await OpenAsync(id);
        };
        Editor.ExternalLinkClicked += url =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        Editor.NewChildNoteRequested += async (title, parentId) =>
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
            await OpenAsync(child.Id);
            return child;
        };
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_note.IsRoot) return;

        var confirmed = await MessageBox.ShowConfirm(this,
            "Move to Trash",
            $"Move \"{_note.Title}\" to Trash?\n\nThe note can be restored from Trash.");
        if (!confirmed) return;

        _deleted = true;
        _closing = true; // skip save
        await App.Notes.DeleteAsync(_note.Id);
        Close();
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await MainWindow.TriggerSyncSaveAsync();
        }
        else if (e.Key == Key.Q && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await CloseAllAsync();
        }
        else if (e.Key == Key.Q && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await SaveAndCloseAsync();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await SaveAndCloseAsync();
        }
    }

    // ── Close: cancel → save → re-close (same pattern as MainWindow) ─────────

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
        if (!_deleted)
            await Editor.DoSave();
        _closing = true;
        Close();
    }
}
