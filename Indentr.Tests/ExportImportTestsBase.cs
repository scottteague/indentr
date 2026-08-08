using System.IO;
using System.Text;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Xunit;

namespace Indentr.Tests;

/// <summary>Export/import integration tests that run against any backend.
/// Concrete subclasses wire up the fixture for their specific backend.</summary>
public abstract class ExportImportTestsBase
{
    protected abstract INoteRepository   Notes       { get; }
    protected abstract IKanbanRepository Kanban      { get; }
    protected abstract IAttachmentStore  Attachments { get; }
    protected abstract User              TestUser    { get; }

    // ── helpers ──────────────────────────────────────────────────────────────

    protected async Task<Note> MakeNote(string title, string content, Guid? parentId = null)
    {
        return await Notes.CreateAsync(new Note
        {
            Title     = title,
            Content   = content,
            ParentId  = parentId,
            OwnerId   = TestUser.Id,
            CreatedBy = TestUser.Id
        });
    }

    protected static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "indentr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    protected async Task<string> DoExportImportRoundTrip(Guid rootNoteId, string destDir)
    {
        var exportedFolder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, rootNoteId, TestUser.Id, destDir);

        await SubtreeImporter.ImportAsync(
            Notes, Kanban, Attachments, exportedFolder, TestUser.Id);

        return exportedFolder;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_SingleNote_TitleAndContentPreserved()
    {
        var note = await MakeNote("Solo Note", "Just some text.");
        var dir  = TempDir();

        await DoExportImportRoundTrip(note.Id, dir);

        var allNotes = await Notes.SearchAsync("Solo Note", TestUser.Id);
        var imported = allNotes.Where(n => n.Id != note.Id).SingleOrDefault(n => n.Title == "Solo Note");
        Assert.NotNull(imported);
        Assert.Equal("Just some text.", imported.Content);
    }

    [Fact]
    public async Task RoundTrip_ParentChild_HierarchyReproduced()
    {
        var parent = await MakeNote("Parent Note", "Intro");
        var child  = await MakeNote("Child Note", "Detail", parent.Id);

        var parentFresh = await Notes.GetByIdAsync(parent.Id);
        parentFresh!.Content = $"Intro [Child Note](note:{child.Id})";
        await Notes.SaveAsync(parentFresh, parentFresh.ContentHash, TestUser.Id);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, parent.Id, TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            Notes, Kanban, Attachments, folder, TestUser.Id);

        Assert.Equal(2, result.NotesImported);

        var parentMatches = await Notes.SearchAsync("Parent Note", TestUser.Id);
        var childMatches  = await Notes.SearchAsync("Child Note",  TestUser.Id);
        Assert.Contains(parentMatches, n => n.Id != parent.Id && n.Title == "Parent Note");
        Assert.Contains(childMatches,  n => n.Id != child.Id  && n.Title == "Child Note");
    }

    [Fact]
    public async Task RoundTrip_NoteLinks_RewrittenToNewIds()
    {
        var noteA = await MakeNote("Note A", "See [Note B](note:00000000-0000-0000-0000-000000000000)");
        var noteB = await MakeNote("Note B", "Referenced by A", noteA.Id);

        var aWithLink = await Notes.GetByIdAsync(noteA.Id);
        aWithLink!.Content = $"See [Note B](note:{noteB.Id})";
        await Notes.SaveAsync(aWithLink, aWithLink.ContentHash, TestUser.Id);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, noteA.Id, TestUser.Id, dir);

        await SubtreeImporter.ImportAsync(
            Notes, Kanban, Attachments, folder, TestUser.Id);

        var importedA = (await Notes.SearchAsync("Note A", TestUser.Id))
            .Where(n => n.Id != noteA.Id).Single(n => n.Title == "Note A");

        var importedB = (await Notes.SearchAsync("Note B", TestUser.Id))
            .Where(n => n.Id != noteB.Id).Single(n => n.Title == "Note B");

        Assert.Contains($"note:{importedB.Id}", importedA.Content);
        Assert.DoesNotContain($"note:{noteB.Id}", importedA.Content);
    }

    [Fact]
    public async Task RoundTrip_KanbanBoard_ColumnsAndCardsPreserved()
    {
        var note  = await MakeNote("Board Host", "");
        var board = await Kanban.CreateBoardAsync("My Board", TestUser.Id);
        var col   = await Kanban.AddColumnAsync(board.Id, "To Do");
        await Kanban.AddCardAsync(col.Id, "Task 1");
        await Kanban.AddCardAsync(col.Id, "Task 2");

        note.Content = $"Board: [My Board](kanban:{board.Id})";
        await Notes.SaveAsync(note, note.ContentHash, TestUser.Id);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, note.Id, TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            Notes, Kanban, Attachments, folder, TestUser.Id);

        Assert.Equal(1, result.BoardsImported);

        var boardFiles = Directory.GetFiles(Path.Combine(folder, "boards"), "*.json");
        Assert.Single(boardFiles);

        var boardJson = await File.ReadAllTextAsync(boardFiles[0], TestContext.Current.CancellationToken);
        Assert.Contains("My Board", boardJson);
        Assert.Contains("To Do",    boardJson);
        Assert.Contains("Task 1",   boardJson);
        Assert.Contains("Task 2",   boardJson);
    }

    [Fact]
    public async Task RoundTrip_Attachment_BytesPreserved()
    {
        var note          = await MakeNote("Note With Attachment", "");
        var originalBytes = Encoding.UTF8.GetBytes("Hello, attachment world!");
        await Attachments.StoreAsync(
            note.Id, "hello.txt", "text/plain",
            new MemoryStream(originalBytes));

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, note.Id, TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            Notes, Kanban, Attachments, folder, TestUser.Id);

        Assert.Equal(1, result.AttachmentsImported);

        var importedNote = (await Notes.SearchAsync("Note With Attachment", TestUser.Id))
            .Single(n => n.Id != note.Id);
        var importedAttachments = await Attachments.ListForNoteAsync(importedNote.Id);
        var importedMeta        = Assert.Single(importedAttachments);
        Assert.Equal("hello.txt", importedMeta.Filename);

        var opened = await Attachments.OpenReadAsync(importedMeta.Id);
        Assert.NotNull(opened);
        await using var stream = opened!.Value.Content;
        var importedBytes = new MemoryStream();
        await stream.CopyToAsync(importedBytes, TestContext.Current.CancellationToken);
        Assert.Equal(originalBytes, importedBytes.ToArray());
    }

    [Fact]
    public async Task Export_ManifestContainsCorrectCounts()
    {
        var root  = await MakeNote("Manifest Root", "");
        var child = await MakeNote("Manifest Child", "", root.Id);
        root.Content = $"[Manifest Child](note:{child.Id})";
        await Notes.SaveAsync(root, root.ContentHash, TestUser.Id);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            Notes, Kanban, Attachments, root.Id, TestUser.Id, dir);

        var manifestJson = await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json"), TestContext.Current.CancellationToken);
        using var doc    = System.Text.Json.JsonDocument.Parse(manifestJson);
        var el           = doc.RootElement;

        Assert.Equal(1,       el.GetProperty("version").GetInt32());
        Assert.Equal(2,       el.GetProperty("noteCount").GetInt32());
        Assert.Equal(root.Id, el.GetProperty("rootNoteId").GetGuid());
    }

    [Fact]
    public async Task Import_InvalidFolder_ThrowsFriendlyError()
    {
        var emptyDir = TempDir();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubtreeImporter.ImportAsync(
                Notes, Kanban, Attachments, emptyDir, TestUser.Id));
        Assert.Contains("manifest.json", ex.Message);
    }
}
