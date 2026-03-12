using System.IO.Compression;
using Indentr.Data;
using Indentr.Data.Repositories;
using Indentr.Web.Config;
using Indentr.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Load static web assets from the SDK manifest (dev) or publish output (prod).
// Required in .NET 9+ for MapStaticAssets() to serve _framework/blazor.server.js.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<NoteChangeNotifier>();
builder.Services.AddScoped<AppSession>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.MapStaticAssets();

// Attachment download endpoint — creates its own store outside circuit scope.
app.MapGet("/api/attachments/{id:guid}", async (Guid id) =>
{
    var profile = ConfigManager.Load().Profiles.FirstOrDefault();
    if (profile is null) return Results.NotFound();

    var schemaName = string.IsNullOrEmpty(profile.LocalSchemaId)
        ? null
        : $"indentr_{profile.LocalSchemaId}";

    var cs = ConnectionStringBuilder.Build(
        profile.Database.Host, profile.Database.Port, profile.Database.Name,
        profile.Database.Username, profile.Database.Password, schemaName);

    var store = new PostgresAttachmentStore(cs);
    var result = await store.OpenReadAsync(id);
    if (result is null) return Results.NotFound();

    var (meta, stream) = result.Value;
    return Results.File(stream, meta.MimeType, meta.Filename);
});

// Subtree export endpoint — exports a note subtree as a .zip archive.
app.MapGet("/api/export/{noteId:guid}", async (Guid noteId) =>
{
    var profile = ConfigManager.Load().Profiles.FirstOrDefault();
    if (profile is null) return Results.NotFound();

    var schemaName = string.IsNullOrEmpty(profile.LocalSchemaId)
        ? null
        : $"indentr_{profile.LocalSchemaId}";

    var cs = ConnectionStringBuilder.Build(
        profile.Database.Host, profile.Database.Port, profile.Database.Name,
        profile.Database.Username, profile.Database.Password, schemaName);

    var noteRepo    = new NoteRepository(cs);
    var kanbanRepo  = new KanbanRepository(cs);
    var attachStore = new PostgresAttachmentStore(cs);
    var userRepo    = new UserRepository(cs);
    var user        = await userRepo.GetOrCreateAsync(profile.Username);

    var tempBase = Path.Combine(Path.GetTempPath(), $"indentr-export-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempBase);
    string? zipPath = null;
    try
    {
        string outFolder;
        try
        {
            outFolder = await SubtreeExporter.ExportAsync(noteRepo, kanbanRepo, attachStore, noteId, user.Id, tempBase);
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(ex.Message);
        }

        var safeName = SubtreeExporter.SafeName(Path.GetFileName(outFolder).Replace("-export", ""));
        zipPath = Path.Combine(Path.GetTempPath(), $"indentr-export-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(outFolder, zipPath);
        var bytes = await File.ReadAllBytesAsync(zipPath);
        return Results.Bytes(bytes, "application/zip", $"{safeName}-export.zip");
    }
    finally
    {
        if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true);
        if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath);
    }
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
