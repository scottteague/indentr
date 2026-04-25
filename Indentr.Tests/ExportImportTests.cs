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

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var cs = _container.GetConnectionString();
        await new DatabaseMigrator(cs).MigrateAsync();
        Notes       = new NoteRepository(cs);
        Kanban      = new KanbanRepository(cs);
        Attachments = new PostgresAttachmentStore(cs);
        TestUser    = await new UserRepository(cs).GetOrCreateAsync("testuser");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[Collection(nameof(DbCollection))]
public class PostgresExportImportTests(DbFixture db) : ExportImportTestsBase
{
    protected override INoteRepository   Notes       => db.Notes;
    protected override IKanbanRepository Kanban      => db.Kanban;
    protected override IAttachmentStore  Attachments => db.Attachments;
    protected override User              TestUser    => db.TestUser;
}
