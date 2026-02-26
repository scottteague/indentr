using Avalonia.Controls;

namespace Indentr.UI.Views;

/// <summary>Simple modal message box helper.</summary>
public static class MessageBox
{
    public static async Task ShowError(Window? owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };

        var button = ((Avalonia.Controls.StackPanel)dlg.Content!).Children.OfType<Button>().First();
        button.Click += (_, _) => dlg.Close();

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();
    }

    public static async Task ShowInfo(Window? owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };

        var button = ((Avalonia.Controls.StackPanel)dlg.Content!).Children.OfType<Button>().First();
        button.Click += (_, _) => dlg.Close();

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();
    }

    public static async Task<bool> ShowConfirm(Window owner, string title, string message)
    {
        bool result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var yesBtn = new Button { Content = "Yes" };
        var noBtn = new Button { Content = "No" };
        yesBtn.Click += (_, _) => { result = true; dlg.Close(); };
        noBtn.Click += (_, _) => { result = false; dlg.Close(); };

        dlg.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new Avalonia.Controls.StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yesBtn, noBtn }
                }
            }
        };

        await dlg.ShowDialog(owner);
        return result;
    }
}
