using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class RecoveryWindow : Window
{
    private readonly Dictionary<string, (Border Row, Button RestoreBtn, Button DiscardBtn)> _rows = new();
    private int _remaining;

    private RecoveryWindow(IReadOnlyList<RecoveryEntry> entries)
    {
        _remaining = entries.Count;
        InitializeComponent();
        BuildRows(entries);
    }

    public static Task ShowAsync(Window owner, IReadOnlyList<RecoveryEntry> entries)
    {
        var win = new RecoveryWindow(entries);
        return win.ShowDialog(owner);
    }

    private void BuildRows(IReadOnlyList<RecoveryEntry> entries)
    {
        foreach (var entry in entries)
        {
            var label = entry.Type == "note"
                ? (entry.Title.Length > 0 ? entry.Title : "Untitled Note")
                : "Scratchpad";

            var (row, restoreBtn, discardBtn) = MakeRow(entry, label);
            _rows[entry.Filename] = (row, restoreBtn, discardBtn);
            RecoveryPanel.Children.Add(row);
        }
        UpdateCloseButton();
    }

    private (Border Row, Button RestoreBtn, Button DiscardBtn) MakeRow(RecoveryEntry entry, string label)
    {
        var titleBlock = new TextBlock
        {
            Text       = label,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };
        var timeBlock = new TextBlock
        {
            Text     = $"Saved {entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            FontSize = 11,
            Opacity  = 0.65
        };

        var infoStack = new StackPanel { Spacing = 2 };
        infoStack.Children.Add(titleBlock);
        infoStack.Children.Add(timeBlock);

        var restoreBtn = new Button { Content = "Restore" };
        var discardBtn = new Button { Content = "Discard" };

        restoreBtn.Click += async (_, _) => await OnRestoreAsync(entry);
        discardBtn.Click += (_, _) => OnDiscard(entry);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnPanel.Children.Add(restoreBtn);
        btnPanel.Children.Add(discardBtn);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btnPanel, Dock.Right);
        dock.Children.Add(btnPanel);
        dock.Children.Add(infoStack);

        var row = new Border
        {
            Padding         = new Avalonia.Thickness(6),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Child           = dock
        };

        return (row, restoreBtn, discardBtn);
    }

    private async Task OnRestoreAsync(RecoveryEntry entry)
    {
        if (!_rows.TryGetValue(entry.Filename, out var ui)) return;
        ui.RestoreBtn.IsEnabled = false;
        ui.DiscardBtn.IsEnabled = false;

        try
        {
            if (entry.Type == "note")
            {
                var note = await App.Notes.GetByIdAsync(entry.Id);
                if (note is not null)
                {
                    note.Title   = entry.Title;
                    note.Content = entry.Content;
                    await App.Notes.SaveAsync(note, note.ContentHash);
                }
                else
                {
                    await App.Notes.CreateAsync(new Note
                    {
                        Title     = entry.Title,
                        Content   = entry.Content,
                        OwnerId   = App.CurrentUser.Id,
                        CreatedBy = App.CurrentUser.Id,
                        SortOrder = 0
                    });
                }
            }
            else // scratchpad
            {
                var scratchpad = await App.Scratchpads.GetOrCreateForUserAsync(entry.Id);
                scratchpad.Content = entry.Content;
                await App.Scratchpads.SaveAsync(scratchpad, scratchpad.ContentHash);
            }

            RecoveryManager.Delete(entry.Filename);
            RemoveRow(entry.Filename);
        }
        catch
        {
            ui.RestoreBtn.IsEnabled = true;
            ui.DiscardBtn.IsEnabled = true;
            await MessageBox.ShowError(this, "Restore Failed",
                "Could not restore this entry. Check your database connection and try again.");
        }
    }

    private void OnDiscard(RecoveryEntry entry)
    {
        RecoveryManager.Delete(entry.Filename);
        RemoveRow(entry.Filename);
    }

    private void RemoveRow(string filename)
    {
        if (!_rows.TryGetValue(filename, out var ui)) return;
        RecoveryPanel.Children.Remove(ui.Row);
        _rows.Remove(filename);
        _remaining--;
        UpdateCloseButton();
    }

    private void UpdateCloseButton() => CloseButton.IsEnabled = _remaining == 0;

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
