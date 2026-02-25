using Avalonia.Controls;
using Avalonia.Interactivity;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class TrashWindow : Window
{
    // Parallel data lists — index matches the ListBox item index.
    private List<Note>         _notes   = new();
    private List<KanbanBoard>  _boards  = new();
    private List<KanbanColumn> _columns = new();
    private List<KanbanCard>   _cards   = new();

    // Active selection — at most one is non-null at a time.
    private Note?         _selectedNote;
    private KanbanBoard?  _selectedBoard;
    private KanbanColumn? _selectedColumn;
    private KanbanCard?   _selectedCard;

    // Board-name lookup for column/card display.
    private Dictionary<Guid, string> _boardNames = new();

    public TrashWindow()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        // ── Notes ──
        _notes = (await App.Notes.GetTrashedAsync(App.CurrentUser.Id)).ToList();
        NotesList.ItemsSource = _notes
            .Select(n => $"{(n.Title.Length > 0 ? n.Title : "(Untitled)")}  —  deleted {n.DeletedAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}")
            .ToList();

        // ── Boards ──
        _boards = (await App.Kanban.GetTrashedBoardsAsync(App.CurrentUser.Id)).ToList();
        _boardNames = _boards.ToDictionary(
            b => b.Id,
            b => b.Title.Length > 0 ? b.Title : "(Untitled Board)");
        BoardsList.ItemsSource = _boards
            .Select(b => $"{(b.Title.Length > 0 ? b.Title : "(Untitled Board)")}  —  deleted {b.DeletedAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}")
            .ToList();

        // ── Columns ── (only those whose board is still active)
        _columns = (await App.Kanban.GetTrashedColumnsAsync(App.CurrentUser.Id)).ToList();
        ColumnsList.ItemsSource = _columns
            .Select(c =>
            {
                var boardName = _boardNames.TryGetValue(c.BoardId, out var bn) ? bn : c.BoardId.ToString()[..8] + "…";
                return $"{c.Title}  in \"{boardName}\"  —  deleted {c.DeletedAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
            })
            .ToList();

        // ── Cards ── (only those whose column and board are both still active)
        _cards = (await App.Kanban.GetTrashedCardsAsync(App.CurrentUser.Id)).ToList();
        var colLookup = _columns.ToDictionary(c => c.Id, c => c.Title);
        CardsList.ItemsSource = _cards
            .Select(k =>
            {
                var colName  = colLookup.TryGetValue(k.ColumnId, out var ct) ? ct : k.ColumnId.ToString()[..8] + "…";
                return $"{k.Title}  in column \"{colName}\"  —  deleted {k.DeletedAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
            })
            .ToList();

        ClearSelection();
    }

    // ── Selection handlers ────────────────────────────────────────────────────

    private void OnNotesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Clear other selections without re-entering this handler
        BoardsList.SelectedIndex  = -1;
        ColumnsList.SelectedIndex = -1;
        CardsList.SelectedIndex   = -1;

        int idx = NotesList.SelectedIndex;
        _selectedNote   = idx >= 0 ? _notes[idx] : null;
        _selectedBoard  = null;
        _selectedColumn = null;
        _selectedCard   = null;
        UpdateButtons();
    }

    private void OnBoardsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        NotesList.SelectedIndex   = -1;
        ColumnsList.SelectedIndex = -1;
        CardsList.SelectedIndex   = -1;

        int idx = BoardsList.SelectedIndex;
        _selectedNote   = null;
        _selectedBoard  = idx >= 0 ? _boards[idx] : null;
        _selectedColumn = null;
        _selectedCard   = null;
        UpdateButtons();
    }

    private void OnColumnsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        NotesList.SelectedIndex  = -1;
        BoardsList.SelectedIndex = -1;
        CardsList.SelectedIndex  = -1;

        int idx = ColumnsList.SelectedIndex;
        _selectedNote   = null;
        _selectedBoard  = null;
        _selectedColumn = idx >= 0 ? _columns[idx] : null;
        _selectedCard   = null;
        UpdateButtons();
    }

    private void OnCardsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        NotesList.SelectedIndex   = -1;
        BoardsList.SelectedIndex  = -1;
        ColumnsList.SelectedIndex = -1;

        int idx = CardsList.SelectedIndex;
        _selectedNote   = null;
        _selectedBoard  = null;
        _selectedColumn = null;
        _selectedCard   = idx >= 0 ? _cards[idx] : null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasSelection = _selectedNote is not null
            || _selectedBoard is not null || _selectedColumn is not null || _selectedCard is not null;
        RestoreButton.IsEnabled    = hasSelection;
        DeletePermButton.IsEnabled = hasSelection;
    }

    private void ClearSelection()
    {
        NotesList.SelectedIndex   = -1;
        BoardsList.SelectedIndex  = -1;
        ColumnsList.SelectedIndex = -1;
        CardsList.SelectedIndex   = -1;
        _selectedNote   = null;
        _selectedBoard  = null;
        _selectedColumn = null;
        _selectedCard   = null;
        UpdateButtons();
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    private async void OnRestoreClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedNote is not null)
            await App.Notes.RestoreAsync(_selectedNote.Id);
        else if (_selectedBoard is not null)
            await App.Kanban.RestoreBoardAsync(_selectedBoard.Id);
        else if (_selectedColumn is not null)
            await App.Kanban.RestoreColumnAsync(_selectedColumn.Id);
        else if (_selectedCard is not null)
            await App.Kanban.RestoreCardAsync(_selectedCard.Id);

        await LoadAsync();
    }

    // ── Delete Permanently ────────────────────────────────────────────────────

    private async void OnDeletePermanentlyClicked(object? sender, RoutedEventArgs e)
    {
        string name = _selectedNote?.Title
            ?? (string?)_selectedBoard?.Title
            ?? _selectedColumn?.Title
            ?? _selectedCard?.Title
            ?? "item";

        var confirmed = await MessageBox.ShowConfirm(this,
            "Delete Permanently",
            $"Permanently delete \"{name}\"? This cannot be undone.");
        if (!confirmed) return;

        if (_selectedNote is not null)
            await App.Notes.PermanentlyDeleteAsync(_selectedNote.Id);
        else if (_selectedBoard is not null)
            await App.Kanban.PermanentlyDeleteBoardAsync(_selectedBoard.Id);
        else if (_selectedColumn is not null)
            await App.Kanban.PermanentlyDeleteColumnAsync(_selectedColumn.Id);
        else if (_selectedCard is not null)
            await App.Kanban.PermanentlyDeleteCardAsync(_selectedCard.Id);

        await LoadAsync();
    }

    // ── Empty Trash ───────────────────────────────────────────────────────────

    private async void OnEmptyTrashClicked(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageBox.ShowConfirm(this,
            "Empty Trash",
            "Permanently delete everything in the Trash? This cannot be undone.");
        if (!confirmed) return;

        // Safe deletion order: cards → columns → boards → notes
        foreach (var c in _cards)   await App.Kanban.PermanentlyDeleteCardAsync(c.Id);
        foreach (var c in _columns) await App.Kanban.PermanentlyDeleteColumnAsync(c.Id);
        foreach (var b in _boards)  await App.Kanban.PermanentlyDeleteBoardAsync(b.Id);
        foreach (var n in _notes)   await App.Notes.PermanentlyDeleteAsync(n.Id);

        await LoadAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e) => await LoadAsync();
}
