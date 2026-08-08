using Indentr.Core.Models;

namespace Indentr.Core.Interfaces;

public enum SaveResult { Success, Conflict, Unauthorized, Error }

public interface INoteRepository
{
    Task<Note?> GetByIdAsync(Guid id);
    Task<Note?> GetRootAsync(Guid userId);
    Task<IEnumerable<NoteTreeNode>> GetChildrenAsync(Guid parentId, Guid userId);
    Task<IEnumerable<Note>> GetOrphansAsync(Guid userId);
    Task<IEnumerable<Note>> SearchAsync(string query, Guid userId);
    Task<Note> CreateAsync(Note note);
    Task<SaveResult> SaveAsync(Note note, string originalHash, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);
    Task<IEnumerable<Note>> GetTrashedAsync(Guid userId);
    Task RestoreAsync(Guid id, Guid userId);
    Task PermanentlyDeleteAsync(Guid id, Guid userId);
    Task EnsureRootExistsAsync(Guid ownerId);
    /// <summary>Returns the root note and all descendants (flat list), privacy-filtered.</summary>
    Task<IReadOnlyList<Note>> GetSubtreeAsync(Guid rootId, Guid userId);

    /// <summary>Updates the display text of every in-app link pointing to noteId
    /// to use newTitle. Returns the IDs of all notes whose content was changed.</summary>
    Task<IReadOnlyList<Guid>> UpdateLinkTitlesAsync(Guid noteId, string newTitle);
}
