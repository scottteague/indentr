using Avalonia.Controls;
using Avalonia.Input;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.UI.Views;

public partial class ScratchpadWindow : Window
{
    private static readonly List<ScratchpadWindow> _openWindows = new();

    public static async Task SaveAllAsync()
    {
        foreach (var win in _openWindows.ToList())
            await win.Editor.DoSave();
    }

    private Scratchpad _scratchpad = null!;
    private bool       _closing;

    public ScratchpadWindow() => InitializeComponent();

    public static async Task OpenAsync()
    {
        var scratchpad = await App.Scratchpads.GetOrCreateForUserAsync(App.CurrentUser.Id);
        var win = new ScratchpadWindow();
        _openWindows.Add(win);
        win.Closed += (_, _) => _openWindows.Remove(win);
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
            RecoveryManager.WriteScratchpad(App.CurrentUser.Id, content);
            try
            {
                var result = await App.Scratchpads.SaveAsync(_scratchpad, originalHash);
                if (result == SaveResult.Success)
                {
                    RecoveryManager.Delete($"scratchpad-{App.CurrentUser.Id}.json");
                    Editor.UpdateOriginalHash(_scratchpad.ContentHash);
                }
                return result;
            }
            catch
            {
                return SaveResult.Error;
            }
        };
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await MainWindow.TriggerSyncSaveAsync();
        }
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
