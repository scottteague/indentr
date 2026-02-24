using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Indentr.Data;
using Indentr.UI.Config;

namespace Indentr.UI.Views;

public partial class FirstRunWindow : Window
{
    private readonly DatabaseProfile _profile;
    private bool _ok;

    // Required by Avalonia's AXAML loader
    public FirstRunWindow() : this(new DatabaseProfile(), isNew: true) { }

    public FirstRunWindow(DatabaseProfile profile, bool isNew)
    {
        _profile = profile;

        if (isNew && string.IsNullOrEmpty(_profile.LocalSchemaId))
            _profile.LocalSchemaId = Guid.NewGuid().ToString("N");

        InitializeComponent();

        if (isNew)
        {
            Title               = "Add Profile";
            HeadingText.Text    = "Add Profile";
            SubheadingText.Text = "Choose a name for this profile and configure its database connection.";
            OkButton.Content    = "Add Profile";
        }
        else
        {
            Title               = "Edit Profile";
            HeadingText.Text    = "Edit Profile";
            SubheadingText.Text = "Update the settings for this profile.";
            OkButton.Content    = "Save";
        }

        ProfileNameBox.Text = profile.Name;
        UsernameBox.Text    = profile.Username;
        DbHostBox.Text      = profile.Database.Host;
        DbPortBox.Text      = profile.Database.Port.ToString();
        DbNameBox.Text      = profile.Database.Name;
        DbUserBox.Text      = profile.Database.Username;
        DbPasswordBox.Text  = profile.Database.Password;

        if (profile.RemoteDatabase is not null)
        {
            EnableRemoteBox.IsChecked   = true;
            RemotePanel.IsVisible       = true;
            RemoteHostBox.Text          = profile.RemoteDatabase.Host;
            RemotePortBox.Text          = profile.RemoteDatabase.Port.ToString();
            RemoteDbNameBox.Text        = profile.RemoteDatabase.Name;
            RemoteDbUserBox.Text        = profile.RemoteDatabase.Username;
            RemoteDbPasswordBox.Text    = profile.RemoteDatabase.Password;
        }
    }

    /// <summary>Shows the window. Pass an owner to make it modal; omit for standalone use.</summary>
    public async Task<bool> ShowDialogAsync(Window? owner = null)
    {
        if (owner is not null)
        {
            await ShowDialog(owner);
            return _ok;
        }

        var tcs = new TaskCompletionSource<bool>();
        Closed += (_, _) => tcs.TrySetResult(_ok);
        Show();
        return await tcs.Task;
    }

    private void OnEnableRemoteChanged(object? sender, RoutedEventArgs e)
    {
        RemotePanel.IsVisible = EnableRemoteBox.IsChecked == true;
        TestResultText.IsVisible = false;
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Profile name cannot be empty.");
            return;
        }

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

        if (EnableRemoteBox.IsChecked == true &&
            (!int.TryParse(RemotePortBox.Text, out var remotePort) || remotePort < 1 || remotePort > 65535))
        {
            ShowError("Remote port must be a number between 1 and 65535.");
            return;
        }

        _profile.Name              = name;
        _profile.Username          = username;
        _profile.Database.Host     = DbHostBox.Text?.Trim() ?? "localhost";
        _profile.Database.Port     = port;
        _profile.Database.Name     = DbNameBox.Text?.Trim() ?? "indentr";
        _profile.Database.Username = DbUserBox.Text?.Trim() ?? "postgres";
        _profile.Database.Password = DbPasswordBox.Text ?? "";

        if (EnableRemoteBox.IsChecked == true)
        {
            int.TryParse(RemotePortBox.Text, out var rPort);
            _profile.RemoteDatabase = new DatabaseConfig
            {
                Host     = RemoteHostBox.Text?.Trim() ?? "localhost",
                Port     = rPort,
                Name     = RemoteDbNameBox.Text?.Trim() ?? "indentr",
                Username = RemoteDbUserBox.Text?.Trim() ?? "postgres",
                Password = RemoteDbPasswordBox.Text ?? ""
            };
        }
        else
        {
            _profile.RemoteDatabase = null;
        }

        _ok = true;
        Close();
    }

    private async void OnTestConnectionClicked(object? sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        TestResultText.IsVisible = false;

        if (!int.TryParse(RemotePortBox.Text, out var port) || port < 1 || port > 65535)
        {
            SetTestResult(success: false, "Invalid port.");
            TestConnectionButton.IsEnabled = true;
            return;
        }

        var cs = ConnectionStringBuilder.Build(
            RemoteHostBox.Text?.Trim() ?? "",
            port,
            RemoteDbNameBox.Text?.Trim() ?? "",
            RemoteDbUserBox.Text?.Trim() ?? "",
            RemoteDbPasswordBox.Text ?? "");

        var error = await ConnectionStringBuilder.TryConnectAsync(cs);
        SetTestResult(success: error is null, error ?? "Connected successfully.");
        TestConnectionButton.IsEnabled = true;
    }

    private void SetTestResult(bool success, string message)
    {
        TestResultText.Text       = message;
        TestResultText.Foreground = success ? Brushes.Green : Brushes.Red;
        TestResultText.IsVisible  = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text      = message;
        ErrorText.IsVisible = true;
    }
}
