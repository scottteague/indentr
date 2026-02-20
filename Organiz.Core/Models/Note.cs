namespace Organiz.Core.Models;

public class Note
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsRoot { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public Guid OwnerId { get; set; }
    public Guid CreatedBy { get; set; }
    public bool IsPrivate { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
