using Avalonia.Controls;
using Avalonia.Interactivity;
using Organiz.UI.Config;

namespace Organiz.UI.Views;

public partial class FirstRunWindow : Window
{
    private readonly AppConfig _config;
    private bool _ok;

    // Parameterless constructor required by Avalonia's AXAML loader
    public FirstRunWindow() : this(new AppConfig()) { }

    public FirstRunWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        // Pre-fill with current values
        UsernameBox.Text = config.Username;
        DbHostBox.Text = config.Database.Host;
        DbPortBox.Text = config.Database.Port.ToString();
        DbNameBox.Text = config.Database.Name;
        DbUserBox.Text = config.Database.Username;
        DbPasswordBox.Text = config.Database.Password;
    }

    public Task<bool> ShowDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        Closed += (_, _) => tcs.TrySetResult(_ok);
        Show();
        return tcs.Task;
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(username))
        {
            ShowError("Username cannot be empty.");
            return;
        }
        if (!int.TryParse(DbPortBox.Text, out var port) || port < 1 || port > 65535)
        {
            ShowError("Port must be a number between 1 and 65535.");
            return;
        }

        _config.Username = username;
        _config.Database.Host = DbHostBox.Text?.Trim() ?? "localhost";
        _config.Database.Port = port;
        _config.Database.Name = DbNameBox.Text?.Trim() ?? "organiz";
        _config.Database.Username = DbUserBox.Text?.Trim() ?? "postgres";
        _config.Database.Password = DbPasswordBox.Text ?? "";

        _ok = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
