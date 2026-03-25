using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Indentr.Core.Models;
using Indentr.Data;
using Npgsql;

namespace Indentr.UI.Views;

public partial class KanbanNoteRecoveryWindow : Window
{
    private record MissingNoteInfo(Guid NoteId, string CardTitle, string ColumnTitle, string BoardTitle);

    private List<MissingNoteInfo> _missing = new();

    public KanbanNoteRecoveryWindow()
    {
        InitializeComponent();

        // Pre-populate source connection string from the configured remote DB if available.
        var remote = App.CurrentProfile.RemoteDatabase;
        if (remote is not null)
            SourceConnBox.Text = ConnectionStringBuilder.Build(
                remote.Host, remote.Port, remote.Name, remote.Username, remote.Password);
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select export folder",
            AllowMultiple = false
        });
        if (folders.Count > 0)
            ExportFolderBox.Text = folders[0].Path.LocalPath;
    }

    private async void OnScanClicked(object? sender, RoutedEventArgs e) => await DoScan();

    private async void OnRecoverClicked(object? sender, RoutedEventArgs e) => await DoRecover();

    // ── Scan ─────────────────────────────────────────────────────────────────

    private async Task DoScan()
    {
        var folder = ExportFolderBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder)) { SetStatus("Please select an export folder first."); return; }

        var boardsDir = Path.Combine(folder, "boards");
        if (!Directory.Exists(boardsDir)) { SetStatus("No boards/ subfolder found — is this a valid export folder?"); return; }

        SetStatus("Scanning…");
        MissingList.ItemsSource = null;
        RecoverButton.IsEnabled = false;

        var all = new List<MissingNoteInfo>();
        foreach (var file in Directory.GetFiles(boardsDir, "*.json"))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var root       = doc.RootElement;
            var boardTitle = root.TryGetProperty("title", out var bt) ? bt.GetString() ?? "(board)" : "(board)";

            if (!root.TryGetProperty("columns", out var cols)) continue;
            foreach (var col in cols.EnumerateArray())
            {
                var colTitle = col.TryGetProperty("title", out var ct) ? ct.GetString() ?? "(column)" : "(column)";
                if (!col.TryGetProperty("cards", out var cards)) continue;
                foreach (var card in cards.EnumerateArray())
                {
                    var cardTitle = card.TryGetProperty("title", out var kt) ? kt.GetString() ?? "(card)" : "(card)";
                    if (card.TryGetProperty("noteId", out var noteIdEl)
                        && noteIdEl.ValueKind != JsonValueKind.Null
                        && Guid.TryParse(noteIdEl.GetString(), out var noteId))
                    {
                        all.Add(new(noteId, cardTitle, colTitle, boardTitle));
                    }
                }
            }
        }

        _missing = new();
        foreach (var info in all)
        {
            var existing = await App.Notes.GetByIdAsync(info.NoteId);
            if (existing is null) _missing.Add(info);
        }

        MissingList.ItemsSource = _missing
            .Select(m => $"{m.BoardTitle}  /  {m.ColumnTitle}  /  \"{m.CardTitle}\"  →  {m.NoteId}")
            .ToList();

        RecoverButton.IsEnabled = _missing.Count > 0;
        SetStatus(_missing.Count == 0
            ? "All kanban-linked notes are present — nothing to recover."
            : $"{_missing.Count} missing note(s) found. Enter the source connection string and click Recover.");
    }

    // ── Recover ───────────────────────────────────────────────────────────────

    private async Task DoRecover()
    {
        var sourceCs = SourceConnBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(sourceCs)) { SetStatus("Please enter the source database connection string."); return; }

        SetStatus("Connecting to source database…");
        RecoverButton.IsEnabled = false;

        var connError = await ConnectionStringBuilder.TryConnectAsync(sourceCs);
        if (connError is not null) { SetStatus($"Could not connect: {connError}"); RecoverButton.IsEnabled = true; return; }

        var idsToFetch = _missing.Select(m => m.NoteId).Distinct().ToList();

        // Fetch notes from the source DB.
        var recovered = new List<Note>();
        await using var sourceConn = new NpgsqlConnection(sourceCs);
        await sourceConn.OpenAsync();

        foreach (var id in idsToFetch)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT id, parent_id, is_root, title, content, content_hash, owner_id, sort_order, " +
                "created_at, updated_at, created_by, is_private, deleted_at " +
                "FROM notes WHERE id = @id AND deleted_at IS NULL", sourceConn);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) continue;
            recovered.Add(new Note
            {
                Id          = r.GetGuid(0),
                ParentId    = r.IsDBNull(1) ? null : r.GetGuid(1),
                IsRoot      = r.GetBoolean(2),
                Title       = r.GetString(3),
                Content     = r.GetString(4),
                ContentHash = r.GetString(5),
                OwnerId     = App.CurrentUser.Id,   // adopt to current user so they can see it
                SortOrder   = r.GetInt32(7),
                CreatedBy   = App.CurrentUser.Id,
                IsPrivate   = r.GetBoolean(11),
            });
        }

        // Insert into local DB (preserving original IDs so existing note: links still work).
        int noteCount = 0;
        var recoveredIds = new HashSet<Guid>();
        foreach (var note in recovered)
        {
            note.IsRoot = false;    // never treat a recovered note as the tree root
            await App.Notes.CreateAsync(note);
            recoveredIds.Add(note.Id);
            noteCount++;
        }

        // Re-link kanban cards: find cards by board+column+card title that still have note_id = NULL.
        var profile    = App.CurrentProfile;
        var schemaName = string.IsNullOrEmpty(profile.LocalSchemaId) ? null : $"indentr_{profile.LocalSchemaId}";
        var localCs    = ConnectionStringBuilder.Build(
            profile.Database.Host, profile.Database.Port,
            profile.Database.Name, profile.Database.Username, profile.Database.Password,
            schemaName);

        int linkCount = 0;
        await using var localConn = new NpgsqlConnection(localCs);
        await localConn.OpenAsync();

        foreach (var info in _missing.Where(m => recoveredIds.Contains(m.NoteId)))
        {
            await using var linkCmd = new NpgsqlCommand(
                @"UPDATE kanban_cards SET note_id = @noteId
                  WHERE title      = @cardTitle
                    AND note_id   IS NULL
                    AND deleted_at IS NULL
                    AND column_id IN (
                        SELECT kc.id FROM kanban_columns kc
                        JOIN kanban_boards kb ON kc.board_id = kb.id
                        WHERE kb.title      = @boardTitle
                          AND kc.title      = @colTitle
                          AND kb.deleted_at IS NULL
                          AND kc.deleted_at IS NULL
                    )", localConn);
            linkCmd.Parameters.AddWithValue("noteId",    info.NoteId);
            linkCmd.Parameters.AddWithValue("cardTitle",  info.CardTitle);
            linkCmd.Parameters.AddWithValue("boardTitle", info.BoardTitle);
            linkCmd.Parameters.AddWithValue("colTitle",   info.ColumnTitle);
            linkCount += await linkCmd.ExecuteNonQueryAsync();
        }

        int notFound = idsToFetch.Count - noteCount;
        var msg = $"Recovered {noteCount} note(s), re-linked {linkCount} card(s).";
        if (notFound > 0) msg += $" ({notFound} ID(s) not found in source DB — may have been deleted.)";

        SetStatus(msg);
        _missing.Clear();
        MissingList.ItemsSource = null;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
