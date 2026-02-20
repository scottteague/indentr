namespace Organiz.Core.Models;

public class Scratchpad
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Content { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
