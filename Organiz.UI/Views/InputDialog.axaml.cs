using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Organiz.UI.Views;

public partial class InputDialog : Window
{
    private string? _result;

    public static async Task<string?> ShowAsync(
        Window owner, string title, string prompt, string initialValue = "")
    {
        var dlg = new InputDialog(title, prompt, initialValue);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private InputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        ValueBox.Text   = initialValue;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    private void OnValueBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            _result = ValueBox.Text?.Trim();
            Close();
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _result = ValueBox.Text?.Trim();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
