using Avalonia.Controls;
using Avalonia.Interactivity;
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
        InitializeComponent();

        if (isNew)
        {
            Title                = "Add Profile";
            HeadingText.Text     = "Add Profile";
            SubheadingText.Text  = "Choose a name for this profile and configure its database connection.";
            OkButton.Content     = "Add Profile";
        }
        else
        {
            Title                = "Edit Profile";
            HeadingText.Text     = "Edit Profile";
            SubheadingText.Text  = "Update the settings for this profile.";
            OkButton.Content     = "Save";
        }

        ProfileNameBox.Text = profile.Name;
        UsernameBox.Text    = profile.Username;
        DbHostBox.Text      = profile.Database.Host;
        DbPortBox.Text      = profile.Database.Port.ToString();
        DbNameBox.Text      = profile.Database.Name;
        DbUserBox.Text      = profile.Database.Username;
        DbPasswordBox.Text  = profile.Database.Password;
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

        _profile.Name              = name;
        _profile.Username          = username;
        _profile.Database.Host     = DbHostBox.Text?.Trim() ?? "localhost";
        _profile.Database.Port     = port;
        _profile.Database.Name     = DbNameBox.Text?.Trim() ?? "indentr";
        _profile.Database.Username = DbUserBox.Text?.Trim() ?? "postgres";
        _profile.Database.Password = DbPasswordBox.Text ?? "";

        _ok = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text      = message;
        ErrorText.IsVisible = true;
    }
}
