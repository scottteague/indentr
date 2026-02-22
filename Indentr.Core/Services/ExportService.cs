using System.Text;
using System.Text.RegularExpressions;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Core.Services;

public class ExportService(INoteRepository noteRepository)
{
    private static readonly Regex InAppLinkPattern =
        new(@"\[([^\]]+)\]\(note:[0-9a-fA-F-]+\)", RegexOptions.Compiled);

    public string ExportNote(Note note) =>
        $"# {note.Title}\n\n{StripInAppLinks(note.Content)}";

    public async Task<string> ExportSubtreeAsync(Note root, Guid userId)
    {
        var sb = new StringBuilder();
        await AppendSubtree(root, 1, sb, userId);
        return sb.ToString();
    }

    private async Task AppendSubtree(Note note, int depth, StringBuilder sb, Guid userId)
    {
        sb.AppendLine($"{new string('#', Math.Min(depth, 6))} {note.Title}");
        sb.AppendLine();
        sb.AppendLine(StripInAppLinks(note.Content));
        sb.AppendLine();

        var children = await noteRepository.GetChildrenAsync(note.Id, userId);
        foreach (var child in children.OrderBy(c => c.SortOrder))
        {
            var childNote = await noteRepository.GetByIdAsync(child.Id);
            if (childNote is not null)
                await AppendSubtree(childNote, depth + 1, sb, userId);
        }
    }

    private static string StripInAppLinks(string content) =>
        InAppLinkPattern.Replace(content, "$1");
}
