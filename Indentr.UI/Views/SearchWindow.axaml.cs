using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class SearchWindow : Window
{
    public SearchWindow() => InitializeComponent();

    private async void OnSearchClicked(object? sender, RoutedEventArgs e) => await DoSearch();

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) await DoSearch();
    }

    private async Task DoSearch()
    {
        var query = QueryBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query)) return;

        StatusText.IsVisible = true;
        StatusText.Text = "Searching…";
        ResultsList.ItemsSource = null;

        var results = (await App.Notes.SearchAsync(query, App.CurrentUser.Id)).ToList();

        StatusText.Text = results.Count == 0
            ? "No results found."
            : $"{results.Count} result(s) found.";

        ResultsList.ItemsSource = results;
    }

    private async void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not Note note) return;
        ResultsList.SelectedItem = null;
        await NotesWindow.OpenAsync(note.Id);
    }
}
