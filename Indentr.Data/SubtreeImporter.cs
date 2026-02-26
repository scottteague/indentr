using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data;

public static class SubtreeImporter
{
    public record ImportResult(int NotesImported, int BoardsImported, int AttachmentsImported);

    private static readonly Regex NoteIdPattern =
        new(@"note:([0-9a-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KanbanIdPattern =
        new(@"kanban:([0-9a-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record ParsedNote(
        Guid   Id,
        string Title,
        Guid?  ParentId,
        bool   IsPrivate,
        int    SortOrder,
        string Content);

    public static async Task<ImportResult> ImportAsync(
        INoteRepository notes,
        IKanbanRepository kanban,
        IAttachmentStore attachments,
        string exportFolder,
        Guid userId)
    {
        // Validate manifest
        var manifestPath = Path.Combine(exportFolder, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Not a valid export folder: manifest.json not found.");

        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        if (!manifestDoc.RootElement.TryGetProperty("version", out var versionEl) || versionEl.GetInt32() != 1)
            throw new InvalidOperationException("Unsupported export format version. Expected version 1.");

        // Parse note files
        var parsedNotes = new List<ParsedNote>();
        var notesDir    = Path.Combine(exportFolder, "notes");
        if (Directory.Exists(notesDir))
            foreach (var file in Directory.GetFiles(notesDir, "*.md"))
                parsedNotes.Add(ParseNoteFile(file));

        // Topological sort: parents before children
        var noteSet   = parsedNotes.Select(n => n.Id).ToHashSet();
        var sorted    = new List<ParsedNote>(parsedNotes.Count);
        var emitted   = new HashSet<Guid>();
        var remaining = new List<ParsedNote>(parsedNotes);

        while (remaining.Count > 0)
        {
            var progress = false;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var n = remaining[i];
                if (n.ParentId is null || !noteSet.Contains(n.ParentId.Value) || emitted.Contains(n.ParentId.Value))
                {
                    sorted.Add(n);
                    emitted.Add(n.Id);
                    remaining.RemoveAt(i);
                    progress = true;
                }
            }
            if (!progress) { sorted.AddRange(remaining); break; }
        }

        // Mint fresh IDs
        var noteIdMap = parsedNotes.ToDictionary(n => n.Id, _ => Guid.NewGuid());

        // Create notes
        foreach (var parsed in sorted)
        {
            Guid? newParentId = parsed.ParentId.HasValue && noteIdMap.TryGetValue(parsed.ParentId.Value, out var np)
                ? np : null;

            await notes.CreateAsync(new Note
            {
                Id        = noteIdMap[parsed.Id],
                ParentId  = newParentId,
                IsRoot    = false,
                Title     = parsed.Title,
                Content   = parsed.Content,
                OwnerId   = userId,
                CreatedBy = userId,
                IsPrivate = parsed.IsPrivate,
                SortOrder = parsed.SortOrder
            });
        }

        // Import boards
        var boardIdMap = new Dictionary<Guid, Guid>();
        var boardsDir  = Path.Combine(exportFolder, "boards");
        int boardCount = 0;
        if (Directory.Exists(boardsDir))
        {
            foreach (var file in Directory.GetFiles(boardsDir, "*.json"))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                var root  = doc.RootElement;
                var oldId = Guid.Parse(root.GetProperty("id").GetString()!);
                var title = root.GetProperty("title").GetString()!;

                var board = await kanban.CreateBoardAsync(title, userId);
                boardIdMap[oldId] = board.Id;

                if (root.TryGetProperty("columns", out var columnsEl))
                {
                    foreach (var colEl in columnsEl.EnumerateArray().OrderBy(c => c.GetProperty("sortOrder").GetInt32()))
                    {
                        var col = await kanban.AddColumnAsync(board.Id, colEl.GetProperty("title").GetString()!);
                        if (colEl.TryGetProperty("cards", out var cardsEl))
                        {
                            foreach (var cardEl in cardsEl.EnumerateArray().OrderBy(c => c.GetProperty("sortOrder").GetInt32()))
                            {
                                var card = await kanban.AddCardAsync(col.Id, cardEl.GetProperty("title").GetString()!);
                                if (cardEl.TryGetProperty("noteId", out var noteIdEl)
                                    && noteIdEl.ValueKind != JsonValueKind.Null
                                    && Guid.TryParse(noteIdEl.GetString(), out var oldNoteId)
                                    && noteIdMap.TryGetValue(oldNoteId, out var newNoteId))
                                {
                                    await kanban.SetCardNoteAsync(card.Id, newNoteId);
                                }
                            }
                        }
                    }
                }
                boardCount++;
            }
        }

        // Link-rewriting pass
        foreach (var parsed in parsedNotes)
        {
            var newId      = noteIdMap[parsed.Id];
            var newContent = NoteIdPattern.Replace(parsed.Content, m =>
            {
                if (Guid.TryParse(m.Groups[1].Value, out var oldId) && noteIdMap.TryGetValue(oldId, out var mapped))
                    return $"note:{mapped}";
                return m.Value;
            });
            newContent = KanbanIdPattern.Replace(newContent, m =>
            {
                if (Guid.TryParse(m.Groups[1].Value, out var oldId) && boardIdMap.TryGetValue(oldId, out var mapped))
                    return $"kanban:{mapped}";
                return m.Value;
            });

            if (newContent != parsed.Content)
            {
                var note = await notes.GetByIdAsync(newId);
                if (note is not null)
                {
                    note.Content = newContent;
                    await notes.SaveAsync(note, note.ContentHash);
                }
            }
        }

        // Import attachments
        int attachmentCount = 0;
        var attachmentsDir  = Path.Combine(exportFolder, "attachments");
        if (Directory.Exists(attachmentsDir))
        {
            foreach (var jsonFile in Directory.GetFiles(attachmentsDir, "*.json"))
            {
                using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFile));
                var root       = doc.RootElement;
                var oldNoteId  = Guid.Parse(root.GetProperty("noteId").GetString()!);
                var filename   = root.GetProperty("filename").GetString()!;
                var mimeType   = root.GetProperty("mimeType").GetString()!;

                if (!noteIdMap.TryGetValue(oldNoteId, out var newNoteId)) continue;

                var attachId = Path.GetFileNameWithoutExtension(jsonFile);
                var binPath  = Path.Combine(attachmentsDir, attachId + ".bin");
                if (!File.Exists(binPath)) continue;

                await using var fs = File.OpenRead(binPath);
                await attachments.StoreAsync(newNoteId, filename, mimeType, fs);
                attachmentCount++;
            }
        }

        return new ImportResult(parsedNotes.Count, boardCount, attachmentCount);
    }

    private static ParsedNote ParseNoteFile(string path)
    {
        var lines       = File.ReadAllLines(path);
        int i           = 0;
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (i < lines.Length && lines[i].Trim() == "---")
        {
            i++;
            while (i < lines.Length && lines[i].Trim() != "---")
            {
                var line  = lines[i++];
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key   = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                    value = value[1..^1].Replace("\\\"", "\"");
                frontmatter[key] = value;
            }
            if (i < lines.Length) i++;
        }

        var content  = string.Join("\n", lines[i..]);
        var id       = frontmatter.TryGetValue("id", out var idStr) && Guid.TryParse(idStr, out var g) ? g : Guid.NewGuid();
        var title    = frontmatter.TryGetValue("title", out var t) ? t : Path.GetFileNameWithoutExtension(path);
        var parentId = frontmatter.TryGetValue("parentId", out var pid) && pid != "null" && Guid.TryParse(pid, out var pg) ? pg : (Guid?)null;
        var isPrivate = frontmatter.TryGetValue("isPrivate", out var priv) && priv.Equals("true", StringComparison.OrdinalIgnoreCase);
        var sortOrder = frontmatter.TryGetValue("sortOrder", out var so) && int.TryParse(so, out var soi) ? soi : 0;

        return new ParsedNote(id, title, parentId, isPrivate, sortOrder, content);
    }
}
