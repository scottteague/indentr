using Organiz.Core.Models;

namespace Organiz.Core.Interfaces;

public enum SaveResult { Success, Conflict }

public interface INoteRepository
{
    Task<Note?> GetByIdAsync(Guid id);
    Task<Note?> GetRootAsync(Guid userId);
    Task<IEnumerable<NoteTreeNode>> GetChildrenAsync(Guid parentId, Guid userId);
    Task<IEnumerable<Note>> GetOrphansAsync(Guid userId);
    Task<IEnumerable<Note>> SearchAsync(string query, Guid userId);
    Task<Note> CreateAsync(Note note);
    Task<SaveResult> SaveAsync(Note note, string originalHash);
    Task DeleteAsync(Guid id);
    Task EnsureRootExistsAsync(Guid ownerId);
    /// <summary>Updates the display text of every in-app link pointing to noteId
    /// to use newTitle. Returns the IDs of all notes whose content was changed.</summary>
    Task<IReadOnlyList<Guid>> UpdateLinkTitlesAsync(Guid noteId, string newTitle);
}
