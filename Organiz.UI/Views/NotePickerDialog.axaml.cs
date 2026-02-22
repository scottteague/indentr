using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Organiz.Core.Models;

namespace Organiz.UI.Views;

public partial class NotePickerDialog : Window
{
    private Note? _result;

    public static async Task<Note?> ShowAsync(Window owner)
    {
        var dlg = new NotePickerDialog();
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private NotePickerDialog() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        QueryBox.Focus();
    }

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) await DoSearchAsync();
    }

    private async Task DoSearchAsync()
    {
        var q = QueryBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q)) return;
        var results = await App.Notes.SearchAsync(q, App.CurrentUser.Id);
        ResultsList.ItemsSource = results;
        OkButton.IsEnabled = false;
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        OkButton.IsEnabled = ResultsList.SelectedItem is Note;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _result = ResultsList.SelectedItem as Note;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
