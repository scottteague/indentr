namespace Indentr.Core.Models;

public class KanbanColumn
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime? DeletedAt { get; set; }
    public List<KanbanCard> Cards { get; set; } = new();
}
