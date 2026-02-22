using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Indentr.UI.Views;

public partial class LinkTargetDialog : Window
{
    private string? _result;

    public LinkTargetDialog() => InitializeComponent();

    public Task<string?> ShowDialogAsync(Window owner)
    {
        var tcs = new TaskCompletionSource<string?>();
        Closed += (_, _) => tcs.TrySetResult(_result);
        return ShowDialog<string?>(owner).ContinueWith(_ => _result);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _result = TargetBox.Text?.Trim();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
