using Indentr.Data;
using Indentr.Data.Repositories;
using Indentr.Web.Components;
using Indentr.Web.Config;
using Indentr.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<NoteChangeNotifier>();
builder.Services.AddScoped<AppSession>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

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

app.MapRazorComponents<Indentr.Web.App>().AddInteractiveServerRenderMode();

app.Run();
