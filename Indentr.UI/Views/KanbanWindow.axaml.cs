using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class KanbanWindow : Window
{
    // ── Static registry: one window per board ────────────────────────────────

    private static readonly Dictionary<Guid, KanbanWindow> _openBoards = new();

    public static async Task OpenAsync(Guid boardId, Guid? sourceNoteId = null)
    {
        if (_openBoards.TryGetValue(boardId, out var existing))
        {
            existing.Activate();
            return;
        }

        var board = await App.Kanban.GetBoardAsync(boardId);
        if (board is null) return;

        var columns = await App.Kanban.GetColumnsWithCardsAsync(boardId);
        var win = new KanbanWindow(board, columns, sourceNoteId);
        _openBoards[boardId] = win;
        win.Closed += (_, _) => _openBoards.Remove(boardId);
        win.Show();
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private KanbanBoard          _board;
    private List<KanbanColumn>   _columns;
    private Guid?                _selectedCardId;
    private Border?              _selectedBorder;
    private readonly Guid?       _sourceNoteId;   // note that hosts the kanban: link
    private bool                 _closing;

    // Rebuilt by BuildBoardUI()
    private readonly List<StackPanel>                  _colCardPanels  = new();
    private readonly Dictionary<Guid, Border>          _cardBorders    = new();
    private readonly List<(KanbanColumn Col, TextBox Box)> _colTitleBoxes = new();

    private KanbanWindow(KanbanBoard board, List<KanbanColumn> columns, Guid? sourceNoteId)
    {
        _board        = board;
        _columns      = columns;
        _sourceNoteId = sourceNoteId;
        InitializeComponent();
        Title              = board.Title.Length > 0 ? board.Title : "Kanban Board";
        BoardTitleBox.Text = board.Title;
        BuildBoardUI();
    }

    // ── Board UI ──────────────────────────────────────────────────────────────

    private void BuildBoardUI()
    {
        ColumnsPanel.Children.Clear();
        _colCardPanels.Clear();
        _cardBorders.Clear();
        _colTitleBoxes.Clear();

        for (int ci = 0; ci < _columns.Count; ci++)
            ColumnsPanel.Children.Add(BuildColumnControl(_columns[ci]));

        // Restore selection highlight after rebuild
        if (_selectedCardId.HasValue && _cardBorders.TryGetValue(_selectedCardId.Value, out var sel))
        {
            _selectedBorder = sel;
            SetCardSelected(sel, true);
        }
        else
        {
            _selectedCardId = null;
            _selectedBorder = null;
        }
    }

    private Control BuildColumnControl(KanbanColumn col)
    {
        // ── Column header ──────────────────────────────────────────────────
        var titleBox = new TextBox
        {
            Text        = col.Title,
            FontWeight  = FontWeight.SemiBold,
            Watermark   = "Column title",
            Margin      = new Thickness(0, 0, 4, 0)
        };
        _colTitleBoxes.Add((col, titleBox));
        titleBox.LostFocus += (_, _) =>
        {
            var newTitle = titleBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newTitle) || newTitle == col.Title) return;
            col.Title = newTitle;
            _ = App.Kanban.UpdateColumnTitleAsync(col.Id, newTitle);
        };

        var deleteColBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(4, 0)
        };
        ToolTip.SetTip(deleteColBtn, "Delete this column");
        deleteColBtn.Click += (_, _) => _ = DeleteColumnAsync(col.Id);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(deleteColBtn, Dock.Right);
        header.Children.Add(deleteColBtn);
        header.Children.Add(titleBox);

        // ── Cards panel ────────────────────────────────────────────────────
        var cardsPanel = new StackPanel { Spacing = 3 };
        _colCardPanels.Add(cardsPanel);

        foreach (var card in col.Cards)
        {
            var cardBorder = BuildCardControl(card);
            cardsPanel.Children.Add(cardBorder);
            _cardBorders[card.Id] = cardBorder;
        }

        var cardsScroll = new ScrollViewer
        {
            Content = cardsPanel,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 460
        };

        // ── Add card button ────────────────────────────────────────────────
        var addCardBtn = new Button
        {
            Content               = "+ Add Card",
            HorizontalAlignment   = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin                = new Thickness(0, 6, 0, 0)
        };
        addCardBtn.Click += (_, _) => _ = AddCardAsync(col.Id);

        // ── Column container ───────────────────────────────────────────────
        var colPanel = new DockPanel { Width = 230 };
        DockPanel.SetDock(header,     Dock.Top);
        DockPanel.SetDock(addCardBtn, Dock.Bottom);
        colPanel.Children.Add(header);
        colPanel.Children.Add(addCardBtn);
        colPanel.Children.Add(cardsScroll);

        return new Border
        {
            Child            = colPanel,
            BorderBrush      = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            BorderThickness  = new Thickness(1),
            CornerRadius     = new CornerRadius(6),
            Padding          = new Thickness(8),
            Background       = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
        };
    }

    private Border BuildCardControl(KanbanCard card)
    {
        var noteHint = card.NoteId.HasValue ? " 🔗" : "";
        var text = new TextBlock
        {
            Text         = card.Title + noteHint,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Thickness(2, 0)
        };

        var border = new Border
        {
            Background      = Brushes.Transparent,
            BorderBrush     = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 5),
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = text
        };

        // Click → select; double-click → rename
        border.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                SelectCard(card.Id, border);
        };
        border.DoubleTapped += (_, _) => _ = OpenOrCreateNoteAsync(card);

        // Context menu
        var cm = new ContextMenu();

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => _ = RenameCardAsync(card);
        cm.Items.Add(renameItem);

        if (card.NoteId.HasValue)
        {
            var noteId = card.NoteId.Value;

            var openItem = new MenuItem { Header = "Open Linked Note" };
            openItem.Click += (_, _) => _ = NotesWindow.OpenAsync(noteId);
            cm.Items.Add(openItem);

            var unlinkItem = new MenuItem { Header = "Unlink Note" };
            unlinkItem.Click += async (_, _) =>
            {
                card.NoteId = null;
                await App.Kanban.SetCardNoteAsync(card.Id, null);
                BuildBoardUI();
            };
            cm.Items.Add(unlinkItem);
        }
        else
        {
            var linkItem = new MenuItem { Header = "Link to Existing Note…" };
            linkItem.Click += (_, _) => _ = LinkToNoteAsync(card);
            cm.Items.Add(linkItem);

            var createItem = new MenuItem { Header = "Create and Link New Note…" };
            createItem.Click += (_, _) => _ = CreateAndLinkNoteAsync(card);
            cm.Items.Add(createItem);
        }

        cm.Items.Add(new Separator());
        var deleteItem = new MenuItem { Header = "Delete Card" };
        deleteItem.Click += (_, _) => _ = DeleteCardAsync(card);
        cm.Items.Add(deleteItem);

        border.ContextMenu = cm;

        return border;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void SelectCard(Guid cardId, Border border)
    {
        if (_selectedBorder != null) SetCardSelected(_selectedBorder, false);
        _selectedCardId = cardId;
        _selectedBorder = border;
        SetCardSelected(border, true);
        // Pull keyboard focus into the window so arrow keys are handled here,
        // not by whatever control had focus before (column title box, editor, etc.)
        ColumnsPanel.Focus();
    }

    private static void SetCardSelected(Border border, bool selected)
    {
        border.Background = selected
            ? new SolidColorBrush(Color.FromRgb(70, 130, 180))
            : Brushes.Transparent;
        if (border.Child is TextBlock tb)
        {
            if (selected)
                tb.Foreground = Brushes.White;
            else
                // ClearValue restores the themed/inherited foreground;
                // setting to null would override the theme with an invisible brush.
                tb.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private (int ColIdx, int CardIdx) FindCardPosition()
    {
        if (_selectedCardId is null) return (-1, -1);
        for (int c = 0; c < _columns.Count; c++)
            for (int k = 0; k < _columns[c].Cards.Count; k++)
                if (_columns[c].Cards[k].Id == _selectedCardId.Value)
                    return (c, k);
        return (-1, -1);
    }

    private void SelectCardAt(int ci, int ki)
    {
        var card = _columns[ci].Cards[ki];
        if (_cardBorders.TryGetValue(card.Id, out var b))
            SelectCard(card.Id, b);
    }

    // ── Keyboard navigation ───────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // Don't intercept keys while a column title TextBox is being edited.
        if (e.Source is TextBox) return;

        var (ci, ki) = FindCardPosition();
        var noMod    = e.KeyModifiers == KeyModifiers.None;
        var shift    = e.KeyModifiers == KeyModifiers.Shift;

        switch (e.Key)
        {
            // Navigate within column
            case Key.Up when noMod && ci >= 0 && ki > 0:
                SelectCardAt(ci, ki - 1);
                e.Handled = true;
                break;
            case Key.Down when noMod && ci >= 0 && ki < _columns[ci].Cards.Count - 1:
                SelectCardAt(ci, ki + 1);
                e.Handled = true;
                break;

            // Navigate between columns
            case Key.Left when noMod && ci > 0:
            {
                var lc = _columns[ci - 1];
                if (lc.Cards.Count > 0) SelectCardAt(ci - 1, Math.Min(ki, lc.Cards.Count - 1));
                e.Handled = true;
                break;
            }
            case Key.Right when noMod && ci >= 0 && ci < _columns.Count - 1:
            {
                var rc = _columns[ci + 1];
                if (rc.Cards.Count > 0) SelectCardAt(ci + 1, Math.Min(ki, rc.Cards.Count - 1));
                e.Handled = true;
                break;
            }

            // Move card within column
            case Key.Up when shift && ci >= 0 && ki > 0:
                _ = MoveCardWithinColumnAsync(ci, ki, ki - 1);
                e.Handled = true;
                break;
            case Key.Down when shift && ci >= 0 && ki < _columns[ci].Cards.Count - 1:
                _ = MoveCardWithinColumnAsync(ci, ki, ki + 1);
                e.Handled = true;
                break;

            // Move card between columns
            case Key.Left when shift && ci > 0:
                _ = MoveCardToColumnAsync(_selectedCardId!.Value, ci, ci - 1);
                e.Handled = true;
                break;
            case Key.Right when shift && ci >= 0 && ci < _columns.Count - 1:
                _ = MoveCardToColumnAsync(_selectedCardId!.Value, ci, ci + 1);
                e.Handled = true;
                break;
            // Open/Create Note
            case Key.Return when noMod && ci >= 0:
                _= OpenOrCreateNoteAsync(_columns[ci].Cards[ki]);
                break;
            // Rename
            case Key.F2 when ci >= 0:
                _ = RenameCardAsync(_columns[ci].Cards[ki]);
                e.Handled = true;
                break;

            // Delete
            case Key.Delete when ci >= 0:
                _ = DeleteCardAsync(_columns[ci].Cards[ki]);
                e.Handled = true;
                break;
        }
    }

    // ── Card operations ───────────────────────────────────────────────────────

    private async Task AddCardAsync(Guid columnId)
    {
        var title = await InputDialog.ShowAsync(this, "Add Card", "Card title:");
        if (string.IsNullOrWhiteSpace(title)) return;

        var card = await App.Kanban.AddCardAsync(columnId, title);
        var col  = _columns.First(c => c.Id == columnId);
        col.Cards.Add(card);

        BuildBoardUI();
        SelectCardAt(_columns.IndexOf(col), col.Cards.Count - 1);
    }

    private async Task RenameCardAsync(KanbanCard card)
    {
        var newTitle = await InputDialog.ShowAsync(this, "Rename Card", "Card title:", card.Title);
        if (string.IsNullOrWhiteSpace(newTitle) || newTitle == card.Title) return;

        card.Title = newTitle;
        await App.Kanban.UpdateCardTitleAsync(card.Id, newTitle);

        BuildBoardUI();
        if (_cardBorders.TryGetValue(card.Id, out var b))
            SelectCard(card.Id, b);
    }

    private async Task LinkToNoteAsync(KanbanCard card)
    {
        var note = await NotePickerDialog.ShowAsync(this);
        if (note is null) return;

        card.NoteId = note.Id;
        await App.Kanban.SetCardNoteAsync(card.Id, note.Id);

        BuildBoardUI();
        if (_cardBorders.TryGetValue(card.Id, out var b))
            SelectCard(card.Id, b);
    }

    private async Task OpenOrCreateNoteAsync(KanbanCard card)
    {
        if (card.NoteId.HasValue)
        {
            await NotesWindow.OpenAsync(card.NoteId.Value);
            return;
        }

        // No linked note yet — create one named after the card and link it.
        var note = await App.Notes.CreateAsync(new Indentr.Core.Models.Note
        {
            Title     = card.Title,
            Content   = "",
            OwnerId   = App.CurrentUser.Id,
            CreatedBy = App.CurrentUser.Id,
            SortOrder = 0
        });

        card.NoteId = note.Id;
        await App.Kanban.SetCardNoteAsync(card.Id, note.Id);

        BuildBoardUI();
        if (_cardBorders.TryGetValue(card.Id, out var b))
            SelectCard(card.Id, b);

        await NotesWindow.OpenAsync(note.Id);
    }

    private async Task CreateAndLinkNoteAsync(KanbanCard card)
    {
        var title = await InputDialog.ShowAsync(this, "Create Note", "Note title:");
        if (string.IsNullOrWhiteSpace(title)) return;

        var note = await App.Notes.CreateAsync(new Indentr.Core.Models.Note
        {
            Title     = title,
            Content   = "",
            OwnerId   = App.CurrentUser.Id,
            CreatedBy = App.CurrentUser.Id,
            SortOrder = 0
        });

        card.NoteId = note.Id;
        await App.Kanban.SetCardNoteAsync(card.Id, note.Id);

        BuildBoardUI();
        if (_cardBorders.TryGetValue(card.Id, out var b))
            SelectCard(card.Id, b);

        await NotesWindow.OpenAsync(note.Id);
    }

    private async Task DeleteCardAsync(KanbanCard card)
    {
        var confirmed = await MessageBox.ShowConfirm(this, "Delete Card", $"Delete \"{card.Title}\"?");
        if (!confirmed) return;

        if (_selectedCardId == card.Id) { _selectedCardId = null; _selectedBorder = null; }

        await App.Kanban.DeleteCardAsync(card.Id);
        var col = _columns.FirstOrDefault(c => c.Cards.Contains(card));
        col?.Cards.Remove(card);
        BuildBoardUI();
    }

    private async Task MoveCardWithinColumnAsync(int ci, int fromIdx, int toIdx)
    {
        var col  = _columns[ci];
        var card = col.Cards[fromIdx];

        col.Cards.RemoveAt(fromIdx);
        col.Cards.Insert(toIdx, card);

        await App.Kanban.RenumberColumnCardsAsync(col.Id, col.Cards.Select(c => c.Id).ToList());

        BuildBoardUI();
        SelectCardAt(ci, toIdx);
    }

    private async Task MoveCardToColumnAsync(Guid cardId, int fromColIdx, int toColIdx)
    {
        var fromCol = _columns[fromColIdx];
        var toCol   = _columns[toColIdx];
        var card    = fromCol.Cards.First(c => c.Id == cardId);

        fromCol.Cards.Remove(card);
        card.ColumnId = toCol.Id;
        toCol.Cards.Add(card);

        await App.Kanban.MoveCardToColumnAsync(cardId, toCol.Id);
        await App.Kanban.RenumberColumnCardsAsync(fromCol.Id, fromCol.Cards.Select(c => c.Id).ToList());
        await App.Kanban.RenumberColumnCardsAsync(toCol.Id,   toCol.Cards.Select(c => c.Id).ToList());

        BuildBoardUI();
        SelectCardAt(toColIdx, toCol.Cards.Count - 1);
    }

    // ── Column operations ─────────────────────────────────────────────────────

    private async void OnAddColumnClick(object? sender, RoutedEventArgs e)
    {
        var title = await InputDialog.ShowAsync(this, "Add Column", "Column title:");
        if (string.IsNullOrWhiteSpace(title)) return;

        var col = await App.Kanban.AddColumnAsync(_board.Id, title);
        _columns.Add(col);
        BuildBoardUI();
    }

    private async Task DeleteColumnAsync(Guid columnId)
    {
        var col = _columns.First(c => c.Id == columnId);
        var msg = col.Cards.Count > 0
            ? $"Delete column \"{col.Title}\" and its {col.Cards.Count} card(s)?"
            : $"Delete column \"{col.Title}\"?";

        var confirmed = await MessageBox.ShowConfirm(this, "Delete Column", msg);
        if (!confirmed) return;

        if (_selectedCardId.HasValue && col.Cards.Any(c => c.Id == _selectedCardId.Value))
        {
            _selectedCardId = null;
            _selectedBorder = null;
        }

        await App.Kanban.DeleteColumnAsync(columnId);
        _columns.Remove(col);
        BuildBoardUI();
    }

    // ── Close: flush pending title edits, then close ─────────────────────────

    public static async Task CloseAllAsync()
    {
        foreach (var win in _openBoards.Values.ToList())
            await win.SaveAndCloseAsync();
    }

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
        await FlushPendingTitleEditsAsync();
        _closing = true;
        Close();
    }

    private async Task FlushPendingTitleEditsAsync()
    {
        var boardTitle = BoardTitleBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(boardTitle) && boardTitle != _board.Title)
        {
            _board.Title = boardTitle;
            Title        = boardTitle;
            await App.Kanban.UpdateBoardTitleAsync(_board.Id, boardTitle);
        }

        foreach (var (col, box) in _colTitleBoxes)
        {
            var colTitle = box.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(colTitle) && colTitle != col.Title)
            {
                col.Title = colTitle;
                await App.Kanban.UpdateColumnTitleAsync(col.Id, colTitle);
            }
        }
    }

    // ── Board title ───────────────────────────────────────────────────────────

    private async void OnBoardTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        var newTitle = BoardTitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(newTitle) || newTitle == _board.Title) return;

        _board.Title = newTitle;
        Title        = newTitle;
        await App.Kanban.UpdateBoardTitleAsync(_board.Id, newTitle);
    }
}
