using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data;

public static class SubtreeExporter
{
    private static readonly Regex KanbanLinkPattern =
        new(@"\(kanban:([0-9a-f-]{36})\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Exports the note subtree rooted at rootNoteId to a folder under destFolder.
    /// Returns the path of the created output folder.</summary>
    public static async Task<string> ExportAsync(
        INoteRepository notes,
        IKanbanRepository kanban,
        IAttachmentStore attachments,
        Guid rootNoteId,
        Guid userId,
        string destFolder)
    {
        var allNotes = await notes.GetSubtreeAsync(rootNoteId, userId);
        if (allNotes.Count == 0)
            throw new InvalidOperationException("Note not found or not accessible.");

        var rootNote = allNotes.First(n => n.Id == rootNoteId);

        var boardIds = new HashSet<Guid>();
        foreach (var note in allNotes)
            foreach (Match m in KanbanLinkPattern.Matches(note.Content))
                if (Guid.TryParse(m.Groups[1].Value, out var bid))
                    boardIds.Add(bid);

        var outFolder = Path.Combine(destFolder, SafeName(rootNote.Title) + "-export");
        Directory.CreateDirectory(Path.Combine(outFolder, "notes"));
        Directory.CreateDirectory(Path.Combine(outFolder, "boards"));
        Directory.CreateDirectory(Path.Combine(outFolder, "attachments"));

        foreach (var note in allNotes)
        {
            var filename = $"{SafeName(note.Title)}-{note.Id}.md";
            await File.WriteAllTextAsync(
                Path.Combine(outFolder, "notes", filename),
                BuildNoteFrontmatter(note) + note.Content,
                Encoding.UTF8);
        }

        int boardCount = 0;
        foreach (var boardId in boardIds)
        {
            var board = await kanban.GetBoardAsync(boardId);
            if (board is null) continue;
            var columns = await kanban.GetColumnsWithCardsAsync(boardId);
            var boardObj = new
            {
                id        = board.Id,
                title     = board.Title,
                ownerId   = board.OwnerId,
                createdAt = board.CreatedAt,
                columns   = columns.Select(c => new
                {
                    id        = c.Id,
                    title     = c.Title,
                    sortOrder = c.SortOrder,
                    cards     = c.Cards.Select(card => new
                    {
                        id        = card.Id,
                        title     = card.Title,
                        noteId    = card.NoteId,
                        sortOrder = card.SortOrder
                    }).ToArray()
                }).ToArray()
            };
            var json     = JsonSerializer.Serialize(boardObj, new JsonSerializerOptions { WriteIndented = true });
            var filename = $"{SafeName(board.Title)}-{board.Id}.json";
            await File.WriteAllTextAsync(Path.Combine(outFolder, "boards", filename), json, Encoding.UTF8);
            boardCount++;
        }

        int attachmentCount = 0;
        foreach (var note in allNotes)
        {
            var metas = await attachments.ListForNoteAsync(note.Id);
            foreach (var meta in metas)
            {
                var result = await attachments.OpenReadAsync(meta.Id);
                if (result is null) continue;

                var sidecar = new { noteId = meta.NoteId, filename = meta.Filename, mimeType = meta.MimeType };
                await File.WriteAllTextAsync(
                    Path.Combine(outFolder, "attachments", $"{meta.Id}.json"),
                    JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);

                var (_, stream) = result.Value;
                await using (stream)
                {
                    await using var fs = File.Create(Path.Combine(outFolder, "attachments", $"{meta.Id}.bin"));
                    await stream.CopyToAsync(fs);
                }
                attachmentCount++;
            }
        }

        var manifest = new
        {
            version         = 1,
            exportedAt      = DateTime.UtcNow,
            rootNoteId      = rootNoteId,
            noteCount       = allNotes.Count,
            boardCount      = boardCount,
            attachmentCount = attachmentCount
        };
        await File.WriteAllTextAsync(
            Path.Combine(outFolder, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

        return outFolder;
    }

    private static string BuildNoteFrontmatter(Note note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {note.Id}");
        sb.AppendLine($"title: {EscapeYaml(note.Title)}");
        sb.AppendLine($"parentId: {(note.ParentId.HasValue ? note.ParentId.Value.ToString() : "null")}");
        sb.AppendLine($"isRoot: {note.IsRoot.ToString().ToLowerInvariant()}");
        sb.AppendLine($"ownerId: {note.OwnerId}");
        sb.AppendLine($"createdBy: {note.CreatedBy}");
        sb.AppendLine($"isPrivate: {note.IsPrivate.ToString().ToLowerInvariant()}");
        sb.AppendLine($"sortOrder: {note.SortOrder}");
        sb.AppendLine($"createdAt: {note.CreatedAt:O}");
        sb.AppendLine($"updatedAt: {note.UpdatedAt:O}");
        sb.AppendLine("---");
        return sb.ToString();
    }

    public static string SafeName(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new StringBuilder();
        foreach (var c in title.Take(40))
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "untitled" : result;
    }

    private static string EscapeYaml(string value)
    {
        if (value.Contains(':') || value.Contains('#') || value.StartsWith('-'))
            return $"\"{value.Replace("\"", "\\\"")}\"";
        return value;
    }
}
