using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data.Sqlite;
using Indentr.Data.Sqlite.Repositories;

namespace Indentr.Tests;

/// <summary>Lightweight fixture that spins up a fresh SQLite database in a temp file.</summary>
public class SqliteDbFixture : IAsyncLifetime
{
    private string _dbPath = "";

    public INoteRepository   Notes       { get; private set; } = null!;
    public IKanbanRepository Kanban      { get; private set; } = null!;
    public IAttachmentStore  Attachments { get; private set; } = null!;
    public User              TestUser    { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indentr_test_{Guid.NewGuid():N}.db");
        await new SqliteDatabaseMigrator(_dbPath).MigrateAsync();

        Notes       = new SqliteNoteRepository(_dbPath);
        Kanban      = new SqliteKanbanRepository(_dbPath);
        Attachments = new SqliteAttachmentStore(_dbPath);
        TestUser    = await new SqliteUserRepository(_dbPath).GetOrCreateAsync("testuser");
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        return Task.CompletedTask;
    }
}

public class SqliteExportImportTests(SqliteDbFixture db) : ExportImportTestsBase, IClassFixture<SqliteDbFixture>
{
    protected override INoteRepository   Notes       => db.Notes;
    protected override IKanbanRepository Kanban      => db.Kanban;
    protected override IAttachmentStore  Attachments => db.Attachments;
    protected override User              TestUser    => db.TestUser;
}
