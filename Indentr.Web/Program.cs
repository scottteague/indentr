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

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
