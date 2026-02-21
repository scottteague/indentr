namespace Organiz.Core.Models;

public class AttachmentMeta
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string Filename { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
}
