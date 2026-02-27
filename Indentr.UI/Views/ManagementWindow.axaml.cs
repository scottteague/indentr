using Avalonia.Controls;
using Avalonia.Interactivity;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class ManagementWindow : Window
{
    private Note?         _selectedOrphan;
    private NoteTreeNode? _selectedTreeNode;
    private NoteTreeNode? _selectedSharedNode;
    private bool          _initialTabSet;

    public ManagementWindow()
    {
        InitializeComponent();
        _ = LoadOrphansAsync();
        _ = LoadTreeAsync();
        _ = LoadSharedNotesAsync();

        // Refresh all lists whenever this window regains focus, so deletions
        // or changes made in a Notes window are immediately reflected here.
        Activated += (_, _) =>
        {
            _ = LoadOrphansAsync();
            _ = LoadTreeAsync();
            _ = LoadSharedNotesAsync();
        };
    }

    // ── Orphan Notes tab ─────────────────────────────────────────────────────

    private async Task LoadOrphansAsync()
    {
        var orphans = await App.Notes.GetOrphansAsync(App.CurrentUser.Id);
        var list = orphans.ToList();
        OrphanList.ItemsSource = list;
        _selectedOrphan = null;
        UpdateOrphanButtons();

        // On first load, default to Tree Browser unless orphans exist
        if (!_initialTabSet)
        {
            _initialTabSet = true;
            if (list.Count == 0)
                TreeTab.IsSelected = true;
        }
    }

    private void OnOrphanSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedOrphan = OrphanList.SelectedItem as Note;
        UpdateOrphanButtons();
    }

    private void UpdateOrphanButtons()
    {
        LinkOrphanButton.IsEnabled   = _selectedOrphan is not null;
        DeleteOrphanButton.IsEnabled = _selectedOrphan is not null;
    }

    private void OnLinkOrphanClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedOrphan is null) return;
        // Switch to Tree Browser and let user pick a parent
        TreeTab.IsSelected = true;
        SelectParentButton.IsEnabled = _selectedTreeNode is not null;
    }

    private async void OnDeleteOrphanClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedOrphan is null) return;
        var ok = await MessageBox.ShowConfirm(this, "Move to Trash",
            $"Move \"{_selectedOrphan.Title}\" to Trash?\nThe note can be restored from Trash.");
        if (!ok) return;

        await App.Notes.DeleteAsync(_selectedOrphan.Id);
        await LoadOrphansAsync();
    }

    private async void OnRefreshOrphans(object? sender, RoutedEventArgs e) =>
        await LoadOrphansAsync();

    // ── Tree Browser tab ─────────────────────────────────────────────────────

    private async Task LoadTreeAsync()
    {
        var root = await App.Notes.GetRootAsync(App.CurrentUser.Id);
        if (root is null) return;

        var rootNode = new NoteTreeNode
        {
            Id          = root.Id,
            Title       = root.Title,
            CreatedBy   = root.CreatedBy,
            HasChildren = true
        };
        await ExpandNodeAsync(rootNode);

        NoteTree.ItemsSource = new[] { rootNode };
    }

    private async Task ExpandNodeAsync(NoteTreeNode node)
    {
        if (node.IsLoaded) return;
        var children = await App.Notes.GetChildrenAsync(node.Id, App.CurrentUser.Id);
        node.Children.Clear();
        node.Children.AddRange(children);
        node.IsLoaded = true;
        node.IsExpanded = node.HasChildren;

        // Pre-load one level so tree shows expand arrows correctly
        foreach (var child in node.Children)
            await ExpandNodeAsync(child);
    }

    private void OnTreeNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedTreeNode = NoteTree.SelectedItem as NoteTreeNode;
        OpenNoteButton.IsEnabled   = _selectedTreeNode is not null;
        SelectParentButton.IsEnabled = _selectedTreeNode is not null && _selectedOrphan is not null;
    }

    private async void OnOpenNoteClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedTreeNode is null) return;
        await NotesWindow.OpenAsync(_selectedTreeNode.Id);
    }

    private async void OnTreeNodeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (NoteTree.SelectedItem is NoteTreeNode node)
            await NotesWindow.OpenAsync(node.Id);
    }

    // ── Shared Notes tab ──────────────────────────────────────────────────────

    private async Task LoadSharedNotesAsync()
    {
        var allUsers = await App.Users.GetAllAsync();
        var roots    = new List<NoteTreeNode>();

        foreach (var user in allUsers)
        {
            if (user.Id == App.CurrentUser.Id) continue;

            var root = await App.Notes.GetRootAsync(user.Id);
            if (root is null) continue;

            // Represent each other user as a top-level tree node labelled by username.
            var userNode = new NoteTreeNode
            {
                Id          = root.Id,
                Title       = user.Username,
                HasChildren = true,
                CreatedBy   = root.CreatedBy,
            };
            await ExpandSharedNodeAsync(userNode);

            // Only include users who have at least one public note visible to us.
            if (userNode.Children.Count > 0 || userNode.HasChildren)
                roots.Add(userNode);
        }

        SharedTree.ItemsSource = roots;
        _selectedSharedNode    = null;
        OpenSharedNoteButton.IsEnabled = false;
    }

    private async Task ExpandSharedNodeAsync(NoteTreeNode node)
    {
        if (node.IsLoaded) return;
        var children = await App.Notes.GetChildrenAsync(node.Id, App.CurrentUser.Id);
        node.Children.Clear();
        node.Children.AddRange(children);
        node.IsLoaded   = true;
        node.IsExpanded = children.Any();

        // Pre-load one level so the tree shows expand arrows correctly.
        foreach (var child in node.Children)
            await ExpandSharedNodeAsync(child);
    }

    private void OnSharedNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedSharedNode            = SharedTree.SelectedItem as NoteTreeNode;
        OpenSharedNoteButton.IsEnabled = _selectedSharedNode is not null;
        CopySharedLinkButton.IsEnabled = _selectedSharedNode is not null;
    }

    private async void OnOpenSharedNoteClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedSharedNode is null) return;
        await NotesWindow.OpenAsync(_selectedSharedNode.Id);
    }

    private async void OnCopySharedLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedSharedNode is null) return;
        var link = $"[{_selectedSharedNode.Title}](note:{_selectedSharedNode.Id})";
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(link);
    }

    private async void OnSharedNodeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (SharedTree.SelectedItem is NoteTreeNode node)
            await NotesWindow.OpenAsync(node.Id);
    }

    private async void OnRefreshShared(object? sender, RoutedEventArgs e) =>
        await LoadSharedNotesAsync();

    private async void OnSelectParentClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedOrphan is null || _selectedTreeNode is null) return;

        // Flush any unsaved edits in the parent's open window before we read from DB,
        // so we don't overwrite the user's in-progress work.
        await NotesWindow.SaveIfOpenAsync(_selectedTreeNode.Id);
        await MainWindow.SaveIfRootAsync(_selectedTreeNode.Id);

        var parent = await App.Notes.GetByIdAsync(_selectedTreeNode.Id);
        if (parent is null) return;

        // Warn if a private note would be linked from a public parent.
        if (_selectedOrphan.IsPrivate && !parent.IsPrivate)
        {
            var proceed = await MessageBox.ShowConfirm(this, "Privacy Mismatch",
                $"\"{_selectedOrphan.Title}\" is private, but \"{parent.Title}\" is public.\n" +
                "Linking a private note from a public note makes it reachable by anyone.\n\n" +
                "Proceed anyway?");
            if (!proceed) return;
        }

        // Append an in-app link to the orphan at the end of the parent note's content.
        // Saving the parent triggers SyncParentLinksAsync, which sets the orphan's parent_id.
        var link      = $"[{_selectedOrphan.Title}](note:{_selectedOrphan.Id})";
        var separator = parent.Content.Length > 0 && !parent.Content.EndsWith('\n') ? "\n" : "";
        parent.Content += separator + link;
        parent.OwnerId  = App.CurrentUser.Id;

        var result = await App.Notes.SaveAsync(parent, parent.ContentHash);
        if (result == SaveResult.Conflict)
        {
            await MessageBox.ShowError(this, "Conflict",
                "The parent note was modified by someone else. Please try again.");
            return;
        }

        // Reload any open window showing the parent so it has the new link and a fresh hash.
        await NotesWindow.ReloadIfOpenAsync(_selectedTreeNode.Id);
        await MainWindow.ReloadIfRootAsync(_selectedTreeNode.Id);

        await MessageBox.ShowError(this, "Linked",
            $"A link to \"{_selectedOrphan.Title}\" was added to \"{_selectedTreeNode.Title}\".");

        _selectedOrphan = null;
        await LoadOrphansAsync();
        await LoadTreeAsync();
    }
}
