namespace Indentr.Core.Models;

public class NoteTreeNode
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
    public bool HasChildren { get; set; }
    public List<NoteTreeNode> Children { get; set; } = [];
    public bool IsLoaded { get; set; }
    public bool IsExpanded { get; set; }
    public Guid CreatedBy { get; set; }
    public bool IsPrivate { get; set; }
}
