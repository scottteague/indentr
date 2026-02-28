using Indentr.Core.Models;

namespace Indentr.Core.Interfaces;

public interface IKanbanRepository
{
    Task<KanbanBoard> CreateBoardAsync(string title, Guid ownerId);
    Task<KanbanBoard?> GetBoardAsync(Guid boardId);
    Task UpdateBoardTitleAsync(Guid boardId, string title);
    Task DeleteBoardAsync(Guid boardId);

    Task<List<KanbanColumn>> GetColumnsWithCardsAsync(Guid boardId);

    Task<KanbanColumn> AddColumnAsync(Guid boardId, string title);
    Task UpdateColumnTitleAsync(Guid columnId, string title);
    Task RenumberColumnsAsync(Guid boardId, IReadOnlyList<Guid> orderedColumnIds);
    Task DeleteColumnAsync(Guid columnId);

    Task<KanbanCard> AddCardAsync(Guid columnId, string title);
    Task UpdateCardTitleAsync(Guid cardId, string title);
    Task SetCardNoteAsync(Guid cardId, Guid? noteId);
    Task DeleteCardAsync(Guid cardId);
    Task MoveCardToColumnAsync(Guid cardId, Guid columnId);
    Task RenumberColumnCardsAsync(Guid columnId, IReadOnlyList<Guid> orderedCardIds);

    Task<IEnumerable<KanbanBoard>>  GetTrashedBoardsAsync(Guid userId);
    Task<IEnumerable<KanbanColumn>> GetTrashedColumnsAsync(Guid userId);
    Task<IEnumerable<KanbanCard>>   GetTrashedCardsAsync(Guid userId);

    Task RestoreBoardAsync(Guid boardId);
    Task RestoreColumnAsync(Guid columnId);
    Task RestoreCardAsync(Guid cardId);

    Task PermanentlyDeleteBoardAsync(Guid boardId);
    Task PermanentlyDeleteColumnAsync(Guid columnId);
    Task PermanentlyDeleteCardAsync(Guid cardId);
}
