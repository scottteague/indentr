using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.UI.Controls.Markdown;
using Indentr.UI.Views;

namespace Indentr.UI.Controls;

public partial class NoteEditorControl : UserControl
{
    // ── State ────────────────────────────────────────────────────────────────
    private Guid?  _noteId;
    private Guid   _userId;
    private Guid   _createdBy;
    private bool   _isRoot;
    private string _originalHash = "";

    // ── Events raised for parent windows ────────────────────────────────────
    public event Action<Guid>?   InAppLinkClicked;
    public event Action<string>? ExternalLinkClicked;

    /// <summary>Raised when "Save" is triggered. Parent handles persistence.</summary>
    public event Func<string /*title*/, string /*content*/, string /*originalHash*/, bool /*isPublic*/, Task<SaveResult>>? SaveRequested;

    /// <summary>Raised by the "+ Note" button. Parent creates the note and calls back with the new note.</summary>
    public event Func<string /*title*/, Guid /*parentNoteId*/, Task<Note?>>? NewChildNoteRequested;

    private static readonly Regex LinkPattern =
        new(@"\[([^\]]*)\]\(([^)]*)\)", RegexOptions.Compiled);

    // Matches list item lines: optional indent, bullet/number, optional checkbox
    // Group 1 = indent  Group 2 = bullet  Group 3 = "[ ] " or "[x] " (if present)
    private static readonly Regex ListItemPattern =
        new(@"^(\s*)([-*]|\d+\.)\s(\[[ xX]\] )?", RegexOptions.Compiled);

    public NoteEditorControl()
    {
        InitializeComponent();
        SetupEditor();
        // Use the tunneling phase so we intercept Enter/Tab before AvaloniaEdit's
        // own input handlers consume them (e.g. Enter inserting a bare newline).
        Editor.TextArea.AddHandler(
            InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.SelectionChanged += (_, _) => UpdateNewChildNoteButton();
    }

    // ── Public load methods ──────────────────────────────────────────────────

    public void LoadNote(Note note, Guid userId)
    {
        _noteId    = note.Id;
        _userId    = userId;
        _createdBy = note.CreatedBy;
        _isRoot    = note.IsRoot;
        _originalHash = note.ContentHash;

        TitleRow.IsVisible      = true;
        TitleBox.Text           = note.Title;
        Editor.Text             = note.Content;
        Editor.IsReadOnly       = false;
        AttachmentBar.IsVisible = true;
        NewBoardButton.IsEnabled = true;
        _ = LoadAttachmentsAsync(note.Id);

        // Privacy checkbox: visible for regular notes editable by their creator; hidden for root.
        PrivacyRow.IsVisible         = !note.IsRoot;
        PublicCheckBox.IsChecked     = !note.IsPrivate;
        PublicCheckBox.IsEnabled     = note.CreatedBy == userId;

        UpdateNewChildNoteButton();
    }

    public void LoadScratchpad(Scratchpad scratchpad, Guid userId)
    {
        _noteId       = null;
        _userId       = userId;
        _originalHash = scratchpad.ContentHash;

        TitleRow.IsVisible      = false;
        PrivacyRow.IsVisible    = false;
        AttachmentBar.IsVisible  = false;
        NewBoardButton.IsEnabled = false;
        Editor.Text              = scratchpad.Content;
        Editor.IsReadOnly       = false;
        UpdateNewChildNoteButton();
    }

    public void UpdateOriginalHash(string newHash) => _originalHash = newHash;

    /// <summary>Updates content and hash in-place without rewiring events. Used when
    /// an external operation (e.g. Management window) modifies this note in the DB.</summary>
    public void RefreshNote(Note note)
    {
        _noteId              = note.Id;
        _originalHash        = note.ContentHash;
        TitleBox.Text        = note.Title;
        Editor.Text          = note.Content;
        PublicCheckBox.IsChecked = !note.IsPrivate;
        UpdateNewChildNoteButton();
    }

    // ── Editor setup ─────────────────────────────────────────────────────────

    private void SetupEditor()
    {
        var monoFamily = new Avalonia.Media.FontFamily("Cascadia Code,Consolas,DejaVu Sans Mono,Liberation Mono,monospace");
        Editor.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer(monoFamily, Editor.FontFamily));
        Editor.Options.EnableHyperlinks      = false; // we handle links ourselves
        Editor.Options.EnableEmailHyperlinks = false;

        // Attach to TextView so the event isn't swallowed by the TextArea first
        Editor.TextArea.TextView.PointerPressed += OnTextViewPointerPressed;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await DoSave();
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            e.Handled = TryContinueListItem();
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
            e.Handled = TryIndentListItem();
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
            e.Handled = TryDedentListItem();
    }

    // ── List keyboard helpers ─────────────────────────────────────────────────

    private bool TryContinueListItem()
    {
        var caret = Editor.CaretOffset;
        var line  = Editor.Document.GetLineByOffset(caret);
        var text  = Editor.Document.GetText(line.Offset, line.Length);

        var m = ListItemPattern.Match(text);
        if (!m.Success) return false;

        var indent      = m.Groups[1].Value;
        var bullet      = m.Groups[2].Value;
        var hasCheckbox = m.Groups[3].Success;
        var content     = text[m.Length..];

        // Empty item: pressing Enter exits the list by stripping the prefix
        if (string.IsNullOrWhiteSpace(content))
        {
            Editor.Document.Replace(line.Offset, line.Length, "");
            Editor.CaretOffset = line.Offset;
            return true;
        }

        // Build the continuation prefix for the new line
        string newPrefix;
        if (hasCheckbox)
            newPrefix = $"{indent}{bullet} [ ] ";
        else if (bullet.EndsWith('.') && int.TryParse(bullet[..^1], out int num))
            newPrefix = $"{indent}{num + 1}. ";
        else
            newPrefix = $"{indent}{bullet} ";

        Editor.Document.Insert(caret, "\n" + newPrefix);
        Editor.CaretOffset = caret + 1 + newPrefix.Length;
        return true;
    }

    private bool TryIndentListItem()
    {
        var caret = Editor.CaretOffset;
        var line  = Editor.Document.GetLineByOffset(caret);
        var text  = Editor.Document.GetText(line.Offset, line.Length);

        if (!ListItemPattern.IsMatch(text)) return false;

        Editor.Document.Insert(line.Offset, "  ");
        Editor.CaretOffset = caret + 2;
        return true;
    }

    private bool TryDedentListItem()
    {
        var caret = Editor.CaretOffset;
        var line  = Editor.Document.GetLineByOffset(caret);
        var text  = Editor.Document.GetText(line.Offset, line.Length);

        if (!ListItemPattern.IsMatch(text)) return false;

        // Remove up to 2 leading spaces
        int spaces = 0;
        while (spaces < 2 && spaces < text.Length && text[spaces] == ' ')
            spaces++;

        if (spaces == 0) return true; // already at leftmost — swallow the event anyway

        Editor.Document.Remove(line.Offset, spaces);
        Editor.CaretOffset = Math.Max(line.Offset, caret - spaces);
        return true;
    }

    // ── Mouse (link clicking) ────────────────────────────────────────────────

    private void OnTextViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Editor.TextArea.TextView).Properties.IsLeftButtonPressed) return;

        // Get the document position from the click point (relative to the TextView)
        var point = e.GetPosition(Editor.TextArea.TextView);
        var pos   = Editor.TextArea.TextView.GetPosition(point);
        if (pos is null) return;

        int offset = Editor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
        var line   = Editor.Document.GetLineByOffset(offset);
        var text   = Editor.Document.GetText(line.Offset, line.Length);

        foreach (Match m in LinkPattern.Matches(text))
        {
            int start = line.Offset + m.Index;
            int end   = start + m.Length;
            if (offset < start || offset > end) continue;

            var target = m.Groups[2].Value;
            if (target.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(target["note:".Length..], out var noteId))
                    InAppLinkClicked?.Invoke(noteId);
            }
            else if (target.StartsWith("kanban:", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(target["kanban:".Length..], out var boardId))
                    _ = KanbanWindow.OpenAsync(boardId, _noteId);
            }
            else
            {
                ExternalLinkClicked?.Invoke(target);
            }
            e.Handled = true;
            break;
        }
    }

    // ── Toolbar button handlers ──────────────────────────────────────────────

    private void OnBoldClick(object? sender, RoutedEventArgs e)      => WrapSelection("**", "**");
    private void OnRedClick(object? sender, RoutedEventArgs e)       => WrapSelection("__", "__");
    private void OnItalicClick(object? sender, RoutedEventArgs e)    => WrapSelection("*", "*");
    private void OnUnderlineClick(object? sender, RoutedEventArgs e) => WrapSelection("_", "_");

    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        var dlg    = new LinkTargetDialog();
        var target = await dlg.ShowDialogAsync(window);
        if (target is null) return;

        var selected = Editor.SelectedText;
        var linkText = string.IsNullOrEmpty(selected) ? target : selected;
        ReplaceSelection($"[{linkText}]({target})");
    }

    private async void OnNewChildNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_noteId is null || NewChildNoteRequested is null) return;
        var title = Editor.SelectedText.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var newNote = await NewChildNoteRequested.Invoke(title, _noteId.Value);
        if (newNote is null) return;

        ReplaceSelection($"[{title}](note:{newNote.Id})");
    }

    private async void OnNewBoardClick(object? sender, RoutedEventArgs e)
    {
        if (_noteId is null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        // Use selected text as the board title, just like new child notes do.
        // Fall back to the input dialog only when nothing is selected.
        var title = Editor.SelectedText.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = await InputDialog.ShowAsync(window, "New Kanban Board", "Board title:");
            if (string.IsNullOrWhiteSpace(title)) return;
        }

        var board = await App.Kanban.CreateBoardAsync(title, _userId);
        ReplaceSelection($"[{title}](kanban:{board.Id})");
        await KanbanWindow.OpenAsync(board.Id, _noteId);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await DoSave();

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        var sp = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Note",
            SuggestedFileName = (TitleBox.Text ?? "note").Trim() + ".md",
            FileTypeChoices   = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
        });
        if (sp is null) return;

        var content = LinkPattern.Replace(Editor.Text, m =>
            m.Groups[2].Value.StartsWith("note:") ? m.Groups[1].Value : m.Value);

        await using var stream = await sp.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        if (TitleRow.IsVisible && !string.IsNullOrWhiteSpace(TitleBox.Text))
            await writer.WriteLineAsync($"# {TitleBox.Text}\n");
        await writer.WriteAsync(content);
    }

    // ── Save logic ───────────────────────────────────────────────────────────

    public async Task<bool> DoSave()
    {
        if (SaveRequested is null) return true;

        var title    = TitleBox.Text ?? "";
        var isPublic = PublicCheckBox.IsChecked == true;
        var result   = await SaveRequested.Invoke(title, Editor.Text, _originalHash, isPublic);

        if (result == SaveResult.Conflict)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            await MessageBox.ShowError(window,
                "Save Conflict",
                "Another user modified this note while you were editing. " +
                "Your version has been saved as a [CONFLICT] note. " +
                "Please merge the two notes manually.");
            return false;
        }

        // Propagate title changes to the display text of every link that points here.
        if (_noteId is not null)
        {
            var affected = await App.Notes.UpdateLinkTitlesAsync(_noteId.Value, title);
            foreach (var id in affected)
            {
                await NotesWindow.ReloadIfOpenAsync(id);
                await MainWindow.ReloadIfRootAsync(id);
            }
        }

        return true;
    }

    // ── Attachments ───────────────────────────────────────────────────────────

    private async Task LoadAttachmentsAsync(Guid noteId)
    {
        AttachmentPanel.Children.Clear();
        var attachments = await App.Attachments.ListForNoteAsync(noteId);
        foreach (var meta in attachments)
            AttachmentPanel.Children.Add(MakeChip(meta));
    }

    private Button MakeChip(AttachmentMeta meta)
    {
        var chip = new Button { Content = meta.Filename };
        chip.Click += (_, _) => _ = OpenWithSystemAppAsync(meta);

        var openItem   = new MenuItem { Header = "Open" };
        var saveItem   = new MenuItem { Header = "Save As…" };
        var deleteItem = new MenuItem { Header = "Delete" };

        openItem.Click   += (_, _) => _ = OpenWithSystemAppAsync(meta);
        saveItem.Click   += (_, _) => _ = SaveAsAsync(meta);
        deleteItem.Click += (_, _) => _ = DeleteAttachmentAsync(meta, chip);

        chip.ContextMenu = new ContextMenu();
        chip.ContextMenu.Items.Add(openItem);
        chip.ContextMenu.Items.Add(saveItem);
        chip.ContextMenu.Items.Add(new Separator());
        chip.ContextMenu.Items.Add(deleteItem);

        return chip;
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_noteId is null) return;

        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title       = "Attach file",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            var meta = await App.Attachments.StoreAsync(
                _noteId.Value, file.Name, "application/octet-stream", stream);
            AttachmentPanel.Children.Add(MakeChip(meta));
        }
    }

    private static async Task OpenWithSystemAppAsync(AttachmentMeta meta)
    {
        var result = await App.Attachments.OpenReadAsync(meta.Id);
        if (result is null) return;

        var ext      = Path.GetExtension(meta.Filename);
        var tempPath = Path.Combine(Path.GetTempPath(), $"indentr_{meta.Id}{ext}");

        var (_, content) = result.Value;
        await using (content)
        {
            await using var fs = File.Create(tempPath);
            await content.CopyToAsync(fs);
        }

        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    private async Task SaveAsAsync(AttachmentMeta meta)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        var sp = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save attachment",
            SuggestedFileName = meta.Filename
        });
        if (sp is null) return;

        var result = await App.Attachments.OpenReadAsync(meta.Id);
        if (result is null) return;

        var (_, content) = result.Value;
        await using (content)
        {
            await using var dest = await sp.OpenWriteAsync();
            await content.CopyToAsync(dest);
        }
    }

    private async Task DeleteAttachmentAsync(AttachmentMeta meta, Button chip)
    {
        var window    = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await MessageBox.ShowConfirm(
            window, "Delete Attachment", $"Delete \"{meta.Filename}\"?");
        if (!confirmed) return;

        await App.Attachments.DeleteAsync(meta.Id);
        AttachmentPanel.Children.Remove(chip);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WrapSelection(string prefix, string suffix)
    {
        var selected = Editor.SelectedText;
        if (selected.StartsWith(prefix) && selected.EndsWith(suffix) && selected.Length > prefix.Length + suffix.Length)
            ReplaceSelection(selected[prefix.Length..^suffix.Length]);
        else
            ReplaceSelection($"{prefix}{selected}{suffix}");
    }

    private void ReplaceSelection(string replacement)
    {
        int start  = Editor.SelectionStart;
        int length = Editor.SelectionLength;
        Editor.Document.Replace(start, length, replacement);
        Editor.SelectionStart  = start;
        Editor.SelectionLength = replacement.Length;
    }

    private void UpdateNewChildNoteButton()
    {
        NewChildNoteButton.IsEnabled = _noteId is not null
                                    && !string.IsNullOrWhiteSpace(Editor.SelectedText);
    }
}
