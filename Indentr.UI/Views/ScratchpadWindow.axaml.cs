using Avalonia.Controls;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class ScratchpadWindow : Window
{
    private Scratchpad _scratchpad = null!;
    private bool       _closing;

    public ScratchpadWindow() => InitializeComponent();

    public static async Task OpenAsync()
    {
        var scratchpad = await App.Scratchpads.GetOrCreateForUserAsync(App.CurrentUser.Id);
        var win = new ScratchpadWindow();
        win.LoadScratchpad(scratchpad);
        win.Show();
    }

    private void LoadScratchpad(Scratchpad scratchpad)
    {
        _scratchpad = scratchpad;
        Editor.LoadScratchpad(scratchpad, App.CurrentUser.Id);

        Editor.SaveRequested += async (_, content, originalHash, _) =>
        {
            _scratchpad.Content = content;
            var result = await App.Scratchpads.SaveAsync(_scratchpad, originalHash);
            if (result == SaveResult.Success)
                Editor.UpdateOriginalHash(_scratchpad.ContentHash);
            return result;
        };
    }

    // ── Close: cancel → save → re-close ─────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _ = SaveAndCloseAsync();
        }
        base.OnClosing(e);
    }

    private async Task SaveAndCloseAsync()
    {
        await Editor.DoSave();
        _closing = true;
        Close();
    }
}
