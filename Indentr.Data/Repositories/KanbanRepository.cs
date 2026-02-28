using Npgsql;
using NpgsqlTypes;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Repositories;

public class KanbanRepository(string connectionString) : IKanbanRepository
{
    // ── Boards ────────────────────────────────────────────────────────────────

    public async Task<KanbanBoard> CreateBoardAsync(string title, Guid ownerId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO kanban_boards (title, owner_id) VALUES (@t, @o) " +
            "RETURNING id, title, owner_id, created_at, updated_at, deleted_at", conn);
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("o", ownerId);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return ReadBoard(r);
    }

    public async Task<KanbanBoard?> GetBoardAsync(Guid boardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE id = @id AND deleted_at IS NULL", conn);
        cmd.Parameters.AddWithValue("id", boardId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadBoard(r) : null;
    }

    public async Task UpdateBoardTitleAsync(Guid boardId, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_boards SET title = @t, updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("id", boardId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    public async Task<List<KanbanColumn>> GetColumnsWithCardsAsync(Guid boardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT c.id, c.board_id, c.title, c.sort_order,
                   k.id        AS card_id,
                   k.column_id AS card_col,
                   k.title     AS card_title,
                   k.note_id,
                   k.sort_order AS card_sort,
                   k.created_at AS card_created
            FROM   kanban_columns c
            LEFT   JOIN kanban_cards k ON k.column_id = c.id AND k.deleted_at IS NULL
            WHERE  c.board_id = @bid
              AND  c.deleted_at IS NULL
            ORDER  BY c.sort_order, c.id, k.sort_order, k.id", conn);
        cmd.Parameters.AddWithValue("bid", boardId);

        var columns = new List<KanbanColumn>();
        KanbanColumn? current = null;

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var colId = r.GetGuid(0);
            if (current is null || current.Id != colId)
            {
                current = new KanbanColumn
                {
                    Id        = colId,
                    BoardId   = r.GetGuid(1),
                    Title     = r.GetString(2),
                    SortOrder = r.GetInt32(3)
                };
                columns.Add(current);
            }

            if (!r.IsDBNull(4)) // card present
            {
                current.Cards.Add(new KanbanCard
                {
                    Id        = r.GetGuid(4),
                    ColumnId  = r.GetGuid(5),
                    Title     = r.GetString(6),
                    NoteId    = r.IsDBNull(7) ? null : r.GetGuid(7),
                    SortOrder = r.GetInt32(8),
                    CreatedAt = r.GetDateTime(9)
                });
            }
        }
        return columns;
    }

    public async Task<KanbanColumn> AddColumnAsync(Guid boardId, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO kanban_columns (board_id, title, sort_order)
            VALUES (@bid, @t, (SELECT COALESCE(MAX(sort_order), -1) + 1
                               FROM kanban_columns WHERE board_id = @bid))
            RETURNING id, board_id, title, sort_order", conn);
        cmd.Parameters.AddWithValue("bid", boardId);
        cmd.Parameters.AddWithValue("t", title);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new KanbanColumn
        {
            Id        = r.GetGuid(0),
            BoardId   = r.GetGuid(1),
            Title     = r.GetString(2),
            SortOrder = r.GetInt32(3)
        };
    }

    public async Task UpdateColumnTitleAsync(Guid columnId, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_columns SET title = @t WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("id", columnId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteColumnAsync(Guid columnId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Cascade soft-delete to all non-deleted cards in this column first.
        await using var cardsCmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET deleted_at = NOW(), updated_at = NOW() WHERE column_id = @cid AND deleted_at IS NULL", conn);
        cardsCmd.Parameters.AddWithValue("cid", columnId);
        await cardsCmd.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_columns SET deleted_at = NOW(), updated_at = NOW() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", columnId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    public async Task<KanbanCard> AddCardAsync(Guid columnId, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO kanban_cards (column_id, title, sort_order)
            VALUES (@cid, @t, (SELECT COALESCE(MAX(sort_order), -1) + 1
                               FROM kanban_cards WHERE column_id = @cid))
            RETURNING id, column_id, title, note_id, sort_order, created_at", conn);
        cmd.Parameters.AddWithValue("cid", columnId);
        cmd.Parameters.AddWithValue("t", title);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new KanbanCard
        {
            Id        = r.GetGuid(0),
            ColumnId  = r.GetGuid(1),
            Title     = r.GetString(2),
            NoteId    = r.IsDBNull(3) ? null : r.GetGuid(3),
            SortOrder = r.GetInt32(4),
            CreatedAt = r.GetDateTime(5)
        };
    }

    public async Task UpdateCardTitleAsync(Guid cardId, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET title = @t WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetCardNoteAsync(Guid cardId, Guid? noteId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET note_id = @nid WHERE id = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("nid", NpgsqlDbType.Uuid)
            { Value = noteId.HasValue ? noteId.Value : DBNull.Value });
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCardAsync(Guid cardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET deleted_at = NOW(), updated_at = NOW() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MoveCardToColumnAsync(Guid cardId, Guid columnId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET column_id = @cid WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("cid", columnId);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenumberColumnCardsAsync(Guid columnId, IReadOnlyList<Guid> orderedCardIds)
    {
        if (orderedCardIds.Count == 0) return;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var batch = new NpgsqlBatch(conn);
        for (int i = 0; i < orderedCardIds.Count; i++)
        {
            var bcmd = new NpgsqlBatchCommand(
                "UPDATE kanban_cards SET sort_order = @ord WHERE id = @id AND column_id = @cid");
            bcmd.Parameters.AddWithValue("ord", i);
            bcmd.Parameters.AddWithValue("id",  orderedCardIds[i]);
            bcmd.Parameters.AddWithValue("cid", columnId);
            batch.BatchCommands.Add(bcmd);
        }
        await batch.ExecuteNonQueryAsync();
    }

    public async Task DeleteBoardAsync(Guid boardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Cascade soft-delete: cards inside this board's columns, then columns, then board.
        await using var cardsCmd = new NpgsqlCommand(
            @"UPDATE kanban_cards SET deleted_at = NOW(), updated_at = NOW()
              WHERE column_id IN (SELECT id FROM kanban_columns WHERE board_id = @bid AND deleted_at IS NULL)
                AND deleted_at IS NULL", conn);
        cardsCmd.Parameters.AddWithValue("bid", boardId);
        await cardsCmd.ExecuteNonQueryAsync();
        await using var colsCmd = new NpgsqlCommand(
            "UPDATE kanban_columns SET deleted_at = NOW(), updated_at = NOW() WHERE board_id = @bid AND deleted_at IS NULL", conn);
        colsCmd.Parameters.AddWithValue("bid", boardId);
        await colsCmd.ExecuteNonQueryAsync();
        await using var boardCmd = new NpgsqlCommand(
            "UPDATE kanban_boards SET deleted_at = NOW(), updated_at = NOW() WHERE id = @id", conn);
        boardCmd.Parameters.AddWithValue("id", boardId);
        await boardCmd.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<KanbanBoard>> GetTrashedBoardsAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE owner_id = @uid AND deleted_at IS NOT NULL ORDER BY deleted_at DESC", conn);
        cmd.Parameters.AddWithValue("uid", userId);
        var boards = new List<KanbanBoard>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            boards.Add(ReadBoard(r));
        return boards;
    }

    public async Task<IEnumerable<KanbanColumn>> GetTrashedColumnsAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Only orphaned columns (not those trashed as part of a board).
        await using var cmd = new NpgsqlCommand(
            @"SELECT c.id, c.board_id, c.title, c.sort_order, c.deleted_at
              FROM kanban_columns c
              JOIN kanban_boards b ON b.id = c.board_id
              WHERE c.deleted_at IS NOT NULL
                AND b.deleted_at IS NULL
                AND b.owner_id = @uid
              ORDER BY c.deleted_at DESC", conn);
        cmd.Parameters.AddWithValue("uid", userId);
        var cols = new List<KanbanColumn>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            cols.Add(new KanbanColumn
            {
                Id        = r.GetGuid(0),
                BoardId   = r.GetGuid(1),
                Title     = r.GetString(2),
                SortOrder = r.GetInt32(3),
                DeletedAt = r.IsDBNull(4) ? null : r.GetDateTime(4)
            });
        return cols;
    }

    public async Task<IEnumerable<KanbanCard>> GetTrashedCardsAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Only orphaned cards (not those trashed as part of a column/board).
        await using var cmd = new NpgsqlCommand(
            @"SELECT k.id, k.column_id, k.title, k.note_id, k.sort_order, k.created_at, k.deleted_at
              FROM kanban_cards k
              JOIN kanban_columns c ON c.id = k.column_id
              JOIN kanban_boards  b ON b.id = c.board_id
              WHERE k.deleted_at IS NOT NULL
                AND c.deleted_at IS NULL
                AND b.deleted_at IS NULL
                AND b.owner_id = @uid
              ORDER BY k.deleted_at DESC", conn);
        cmd.Parameters.AddWithValue("uid", userId);
        var cards = new List<KanbanCard>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            cards.Add(new KanbanCard
            {
                Id        = r.GetGuid(0),
                ColumnId  = r.GetGuid(1),
                Title     = r.GetString(2),
                NoteId    = r.IsDBNull(3) ? null : r.GetGuid(3),
                SortOrder = r.GetInt32(4),
                CreatedAt = r.GetDateTime(5),
                DeletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6)
            });
        return cards;
    }

    public async Task RestoreBoardAsync(Guid boardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Restore board, its columns, and their cards.
        await using var cardsCmd = new NpgsqlCommand(
            @"UPDATE kanban_cards SET deleted_at = NULL, updated_at = NOW()
              WHERE column_id IN (SELECT id FROM kanban_columns WHERE board_id = @bid)
                AND deleted_at IS NOT NULL", conn);
        cardsCmd.Parameters.AddWithValue("bid", boardId);
        await cardsCmd.ExecuteNonQueryAsync();
        await using var colsCmd = new NpgsqlCommand(
            "UPDATE kanban_columns SET deleted_at = NULL, updated_at = NOW() WHERE board_id = @bid", conn);
        colsCmd.Parameters.AddWithValue("bid", boardId);
        await colsCmd.ExecuteNonQueryAsync();
        await using var boardCmd = new NpgsqlCommand(
            "UPDATE kanban_boards SET deleted_at = NULL, updated_at = NOW() WHERE id = @id", conn);
        boardCmd.Parameters.AddWithValue("id", boardId);
        await boardCmd.ExecuteNonQueryAsync();
    }

    public async Task RestoreColumnAsync(Guid columnId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cardsCmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET deleted_at = NULL, updated_at = NOW() WHERE column_id = @cid AND deleted_at IS NOT NULL", conn);
        cardsCmd.Parameters.AddWithValue("cid", columnId);
        await cardsCmd.ExecuteNonQueryAsync();
        await using var colCmd = new NpgsqlCommand(
            "UPDATE kanban_columns SET deleted_at = NULL, updated_at = NOW() WHERE id = @id", conn);
        colCmd.Parameters.AddWithValue("id", columnId);
        await colCmd.ExecuteNonQueryAsync();
    }

    public async Task RestoreCardAsync(Guid cardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE kanban_cards SET deleted_at = NULL, updated_at = NOW() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteBoardAsync(Guid boardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // DB ON DELETE CASCADE handles columns and cards.
        await using var cmd = new NpgsqlCommand("DELETE FROM kanban_boards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", boardId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteColumnAsync(Guid columnId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // DB ON DELETE CASCADE handles cards.
        await using var cmd = new NpgsqlCommand("DELETE FROM kanban_columns WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", columnId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteCardAsync(Guid cardId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM kanban_cards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KanbanBoard ReadBoard(NpgsqlDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        Title     = r.GetString(1),
        OwnerId   = r.GetGuid(2),
        CreatedAt = r.GetDateTime(3),
        UpdatedAt = r.GetDateTime(4),
        DeletedAt = r.IsDBNull(5) ? null : r.GetDateTime(5)
    };
}
