using Moq;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Core.Services;

namespace Indentr.Tests;

public class ExportServiceTests
{
    private static readonly Guid SomeId = Guid.NewGuid();

    [Fact]
    public void ExportNote_StripsInAppLinks()
    {
        var repoMock = new Mock<INoteRepository>();
        var svc = new ExportService(repoMock.Object);

        var note = new Note
        {
            Title   = "Test",
            Content = "Hello [world](note:550e8400-e29b-41d4-a716-446655440000) and [Google](https://google.com)"
        };

        var result = svc.ExportNote(note);

        Assert.Contains("Hello world", result);                    // in-app link stripped to text
        Assert.Contains("[Google](https://google.com)", result);   // external link kept
        Assert.DoesNotContain("note:", result);
    }

    [Fact]
    public void ExportNote_IncludesTitle()
    {
        var repoMock = new Mock<INoteRepository>();
        var svc = new ExportService(repoMock.Object);

        var note = new Note { Title = "My Note", Content = "Body text" };
        var result = svc.ExportNote(note);

        Assert.StartsWith("# My Note", result);
    }
}
