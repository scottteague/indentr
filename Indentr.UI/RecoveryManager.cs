using System.Text.Json;
using System.Text.Json.Serialization;

namespace Indentr.UI;

public record RecoveryEntry(
    string         Filename,
    string         Type,
    Guid           Id,
    string         Title,
    string         Content,
    DateTimeOffset SavedAt);

public static class RecoveryManager
{
    private static readonly string RecoveryDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "indentr", "recovery");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void WriteNote(Guid noteId, string title, string content)
    {
        Directory.CreateDirectory(RecoveryDir);
        var obj  = new RecoveryFile("note", noteId, title, content, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(obj, JsonOptions);
        File.WriteAllText(Path.Combine(RecoveryDir, $"note-{noteId}.json"), json);
    }

    public static void WriteScratchpad(Guid userId, string content)
    {
        Directory.CreateDirectory(RecoveryDir);
        var obj  = new RecoveryFile("scratchpad", userId, "Scratchpad", content, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(obj, JsonOptions);
        File.WriteAllText(Path.Combine(RecoveryDir, $"scratchpad-{userId}.json"), json);
    }

    public static void Delete(string filename)
    {
        try { File.Delete(Path.Combine(RecoveryDir, filename)); }
        catch { /* best effort */ }
    }

    public static IReadOnlyList<RecoveryEntry> Scan()
    {
        if (!Directory.Exists(RecoveryDir))
            return [];

        var entries = new List<RecoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(RecoveryDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var obj  = JsonSerializer.Deserialize<RecoveryFile>(json, JsonOptions);
                if (obj is null) continue;

                entries.Add(new RecoveryEntry(
                    Path.GetFileName(file),
                    obj.Type,
                    obj.Id,
                    obj.Title,
                    obj.Content,
                    obj.SavedAt));
            }
            catch { /* skip corrupt files */ }
        }
        return entries;
    }

    private sealed record RecoveryFile(
        [property: JsonPropertyName("type")]    string         Type,
        [property: JsonPropertyName("id")]      Guid           Id,
        [property: JsonPropertyName("title")]   string         Title,
        [property: JsonPropertyName("content")] string         Content,
        [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt);
}
