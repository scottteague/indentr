using System.IO;
using System.Text;
using DotNet.Testcontainers.Builders;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Indentr.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Indentr.Tests;

// One Postgres container shared across all tests in the collection for speed.
[CollectionDefinition(nameof(DbCollection))]
public class DbCollection : ICollectionFixture<DbFixture> { }

public class DbFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("indentr_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public INoteRepository    Notes       { get; private set; } = null!;
    public IKanbanRepository  Kanban      { get; private set; } = null!;
    public IAttachmentStore   Attachments { get; private set; } = null!;
    public User               TestUser    { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var cs = _container.GetConnectionString();
        await new DatabaseMigrator(cs).MigrateAsync();
        Notes       = new NoteRepository(cs);
        Kanban      = new KanbanRepository(cs);
        Attachments = new PostgresAttachmentStore(cs);
        TestUser    = await new UserRepository(cs).GetOrCreateAsync("testuser");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Collection(nameof(DbCollection))]
public class ExportImportTests(DbFixture db)
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Note> MakeNote(string title, string content, Guid? parentId = null)
    {
        return await db.Notes.CreateAsync(new Note
        {
            Title     = title,
            Content   = content,
            ParentId  = parentId,
            OwnerId   = db.TestUser.Id,
            CreatedBy = db.TestUser.Id
        });
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "indentr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static async Task<string> DoExportImportRoundTrip(
        DbFixture db, Guid rootNoteId, string destDir)
    {
        var exportedFolder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            rootNoteId, db.TestUser.Id, destDir);

        await SubtreeImporter.ImportAsync(
            db.Notes, db.Kanban, db.Attachments,
            exportedFolder, db.TestUser.Id);

        return exportedFolder;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_SingleNote_TitleAndContentPreserved()
    {
        var note = await MakeNote("Solo Note", "Just some text.");
        var dir  = TempDir();

        await DoExportImportRoundTrip(db, note.Id, dir);

        var allNotes = await db.Notes.SearchAsync("Solo Note", db.TestUser.Id);
        var imported = allNotes.Where(n => n.Id != note.Id).SingleOrDefault(n => n.Title == "Solo Note");
        Assert.NotNull(imported);
        Assert.Equal("Just some text.", imported.Content);
    }

    [Fact]
    public async Task RoundTrip_ParentChild_HierarchyReproduced()
    {
        var parent = await MakeNote("Parent Note", "Intro");
        var child  = await MakeNote("Child Note", "Detail", parent.Id);

        // Re-save parent with a link so the recursive CTE picks up child via parent_id
        var parentFresh = await db.Notes.GetByIdAsync(parent.Id);
        parentFresh!.Content = $"Intro [Child Note](note:{child.Id})";
        await db.Notes.SaveAsync(parentFresh, parentFresh.ContentHash);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            parent.Id, db.TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            db.Notes, db.Kanban, db.Attachments,
            folder, db.TestUser.Id);

        Assert.Equal(2, result.NotesImported);

        // Both titles appear in search
        var parentMatches = await db.Notes.SearchAsync("Parent Note", db.TestUser.Id);
        var childMatches  = await db.Notes.SearchAsync("Child Note",  db.TestUser.Id);
        Assert.Contains(parentMatches, n => n.Id != parent.Id && n.Title == "Parent Note");
        Assert.Contains(childMatches,  n => n.Id != child.Id  && n.Title == "Child Note");
    }

    [Fact]
    public async Task RoundTrip_NoteLinks_RewrittenToNewIds()
    {
        var noteA = await MakeNote("Note A", "See [Note B](note:00000000-0000-0000-0000-000000000000)");
        var noteB = await MakeNote("Note B", "Referenced by A", noteA.Id);

        // Re-save A with the real link to B
        var aWithLink = await db.Notes.GetByIdAsync(noteA.Id);
        aWithLink!.Content = $"See [Note B](note:{noteB.Id})";
        await db.Notes.SaveAsync(aWithLink, aWithLink.ContentHash);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            noteA.Id, db.TestUser.Id, dir);

        await SubtreeImporter.ImportAsync(
            db.Notes, db.Kanban, db.Attachments,
            folder, db.TestUser.Id);

        // Find the imported copy of Note A
        var importedA = (await db.Notes.SearchAsync("Note A", db.TestUser.Id))
            .Where(n => n.Id != noteA.Id).Single(n => n.Title == "Note A");

        // Find the imported copy of Note B
        var importedB = (await db.Notes.SearchAsync("Note B", db.TestUser.Id))
            .Where(n => n.Id != noteB.Id).Single(n => n.Title == "Note B");

        // The link inside importedA should point to importedB's new ID, not the old one
        Assert.Contains($"note:{importedB.Id}", importedA.Content);
        Assert.DoesNotContain($"note:{noteB.Id}", importedA.Content);
    }

    [Fact]
    public async Task RoundTrip_KanbanBoard_ColumnsAndCardsPreserved()
    {
        var note  = await MakeNote("Board Host", "");
        var board = await db.Kanban.CreateBoardAsync("My Board", db.TestUser.Id);
        var col   = await db.Kanban.AddColumnAsync(board.Id, "To Do");
        await db.Kanban.AddCardAsync(col.Id, "Task 1");
        await db.Kanban.AddCardAsync(col.Id, "Task 2");

        // Link the board in the note content so the exporter picks it up
        note.Content = $"Board: [My Board](kanban:{board.Id})";
        await db.Notes.SaveAsync(note, note.ContentHash);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            note.Id, db.TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            db.Notes, db.Kanban, db.Attachments,
            folder, db.TestUser.Id);

        Assert.Equal(1, result.BoardsImported);

        // The board JSON file should exist
        var boardFiles = Directory.GetFiles(Path.Combine(folder, "boards"), "*.json");
        Assert.Single(boardFiles);

        // Read the board file and verify structure
        var boardJson = await File.ReadAllTextAsync(boardFiles[0]);
        Assert.Contains("My Board", boardJson);
        Assert.Contains("To Do",    boardJson);
        Assert.Contains("Task 1",   boardJson);
        Assert.Contains("Task 2",   boardJson);
    }

    [Fact]
    public async Task RoundTrip_Attachment_BytesPreserved()
    {
        var note           = await MakeNote("Note With Attachment", "");
        var originalBytes  = Encoding.UTF8.GetBytes("Hello, attachment world!");
        await db.Attachments.StoreAsync(
            note.Id, "hello.txt", "text/plain",
            new MemoryStream(originalBytes));

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            note.Id, db.TestUser.Id, dir);

        var result = await SubtreeImporter.ImportAsync(
            db.Notes, db.Kanban, db.Attachments,
            folder, db.TestUser.Id);

        Assert.Equal(1, result.AttachmentsImported);

        // Find the imported note and verify its attachment bytes
        var importedNote = (await db.Notes.SearchAsync("Note With Attachment", db.TestUser.Id))
            .Single(n => n.Id != note.Id);
        var importedAttachments = await db.Attachments.ListForNoteAsync(importedNote.Id);
        var importedMeta        = Assert.Single(importedAttachments);
        Assert.Equal("hello.txt", importedMeta.Filename);

        var opened = await db.Attachments.OpenReadAsync(importedMeta.Id);
        Assert.NotNull(opened);
        await using var stream = opened!.Value.Content;
        var importedBytes = new MemoryStream();
        await stream.CopyToAsync(importedBytes);
        Assert.Equal(originalBytes, importedBytes.ToArray());
    }

    [Fact]
    public async Task Export_ManifestContainsCorrectCounts()
    {
        var root  = await MakeNote("Manifest Root", "");
        var child = await MakeNote("Manifest Child", "", root.Id);
        root.Content = $"[Manifest Child](note:{child.Id})";
        await db.Notes.SaveAsync(root, root.ContentHash);

        var dir    = TempDir();
        var folder = await SubtreeExporter.ExportAsync(
            db.Notes, db.Kanban, db.Attachments,
            root.Id, db.TestUser.Id, dir);

        var manifestJson = await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json"));
        using var doc    = System.Text.Json.JsonDocument.Parse(manifestJson);
        var el           = doc.RootElement;

        Assert.Equal(1,    el.GetProperty("version").GetInt32());
        Assert.Equal(2,    el.GetProperty("noteCount").GetInt32());
        Assert.Equal(root.Id, el.GetProperty("rootNoteId").GetGuid());
    }

    [Fact]
    public async Task Import_InvalidFolder_ThrowsFriendlyError()
    {
        var emptyDir = TempDir();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubtreeImporter.ImportAsync(
                db.Notes, db.Kanban, db.Attachments,
                emptyDir, db.TestUser.Id));
        Assert.Contains("manifest.json", ex.Message);
    }
}
