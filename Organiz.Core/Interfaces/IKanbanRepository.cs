using Organiz.Core.Models;

namespace Organiz.Core.Interfaces;

public interface IKanbanRepository
{
    Task<KanbanBoard> CreateBoardAsync(string title, Guid ownerId);
    Task<KanbanBoard?> GetBoardAsync(Guid boardId);
    Task UpdateBoardTitleAsync(Guid boardId, string title);

    Task<List<KanbanColumn>> GetColumnsWithCardsAsync(Guid boardId);

    Task<KanbanColumn> AddColumnAsync(Guid boardId, string title);
    Task UpdateColumnTitleAsync(Guid columnId, string title);
    Task DeleteColumnAsync(Guid columnId);

    Task<KanbanCard> AddCardAsync(Guid columnId, string title);
    Task UpdateCardTitleAsync(Guid cardId, string title);
    Task SetCardNoteAsync(Guid cardId, Guid? noteId);
    Task DeleteCardAsync(Guid cardId);
    Task MoveCardToColumnAsync(Guid cardId, Guid columnId);
    Task RenumberColumnCardsAsync(Guid columnId, IReadOnlyList<Guid> orderedCardIds);
}
