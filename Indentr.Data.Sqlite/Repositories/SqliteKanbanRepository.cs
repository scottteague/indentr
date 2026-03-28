using Microsoft.Data.Sqlite;
using Indentr.Core.Interfaces;
using Indentr.Core.Models;

namespace Indentr.Data.Sqlite.Repositories;

public class SqliteKanbanRepository(string dbPath) : IKanbanRepository
{
    // ── Boards ────────────────────────────────────────────────────────────────

    public async Task<KanbanBoard> CreateBoardAsync(string title, Guid ownerId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var cmd = new SqliteCommand(
            "INSERT INTO kanban_boards (id, title, owner_id, created_at, updated_at) VALUES (@id, @t, @o, @now, @now)",
            conn);
        cmd.Parameters.AddWithValue("@id",  id);
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@o",   ownerId.ToString());
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();
        return new KanbanBoard
        {
            Id        = Guid.Parse(id),
            Title     = title,
            OwnerId   = ownerId,
            CreatedAt = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<KanbanBoard?> GetBoardAsync(Guid boardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE id = @id AND deleted_at IS NULL",
            conn);
        cmd.Parameters.AddWithValue("@id", boardId.ToString());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadBoard(r) : null;
    }

    public async Task UpdateBoardTitleAsync(Guid boardId, string title)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_boards SET title = @t, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  boardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBoardAsync(Guid boardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var now = SqliteHelper.UtcNow();
        // Cascade soft-delete: cards, then columns, then board.
        await using var cardsCmd = new SqliteCommand(
            @"UPDATE kanban_cards SET deleted_at = @now, updated_at = @now
              WHERE column_id IN (SELECT id FROM kanban_columns WHERE board_id = @bid AND deleted_at IS NULL)
                AND deleted_at IS NULL",
            conn);
        cardsCmd.Parameters.AddWithValue("@now", now);
        cardsCmd.Parameters.AddWithValue("@bid", boardId.ToString());
        await cardsCmd.ExecuteNonQueryAsync();

        await using var colsCmd = new SqliteCommand(
            "UPDATE kanban_columns SET deleted_at = @now, updated_at = @now WHERE board_id = @bid AND deleted_at IS NULL",
            conn);
        colsCmd.Parameters.AddWithValue("@now", now);
        colsCmd.Parameters.AddWithValue("@bid", boardId.ToString());
        await colsCmd.ExecuteNonQueryAsync();

        await using var boardCmd = new SqliteCommand(
            "UPDATE kanban_boards SET deleted_at = @now, updated_at = @now WHERE id = @id", conn);
        boardCmd.Parameters.AddWithValue("@now", now);
        boardCmd.Parameters.AddWithValue("@id",  boardId.ToString());
        await boardCmd.ExecuteNonQueryAsync();
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    public async Task<List<KanbanColumn>> GetColumnsWithCardsAsync(Guid boardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            @"SELECT c.id, c.board_id, c.title, c.sort_order,
                     k.id, k.column_id, k.title, k.note_id, k.sort_order, k.created_at
              FROM   kanban_columns c
              LEFT   JOIN kanban_cards k ON k.column_id = c.id AND k.deleted_at IS NULL
              WHERE  c.board_id = @bid AND c.deleted_at IS NULL
              ORDER  BY c.sort_order, c.id, k.sort_order, k.id",
            conn);
        cmd.Parameters.AddWithValue("@bid", boardId.ToString());

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
            if (!r.IsDBNull(4))
            {
                current.Cards.Add(new KanbanCard
                {
                    Id        = r.GetGuid(4),
                    ColumnId  = r.GetGuid(5),
                    Title     = r.GetString(6),
                    NoteId    = r.GetNullableGuid(7),
                    SortOrder = r.GetInt32(8),
                    CreatedAt = r.GetDateTime(9)
                });
            }
        }
        return columns;
    }

    public async Task<KanbanColumn> AddColumnAsync(Guid boardId, string title)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var cmd = new SqliteCommand(
            @"INSERT INTO kanban_columns (id, board_id, title, sort_order, updated_at)
              VALUES (@id, @bid, @t,
                      (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM kanban_columns WHERE board_id = @bid),
                      @now)",
            conn);
        cmd.Parameters.AddWithValue("@id",  id);
        cmd.Parameters.AddWithValue("@bid", boardId.ToString());
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();

        await using var sel = new SqliteCommand(
            "SELECT id, board_id, title, sort_order FROM kanban_columns WHERE id = @id", conn);
        sel.Parameters.AddWithValue("@id", id);
        await using var r = await sel.ExecuteReaderAsync();
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
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_columns SET title = @t, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  columnId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenumberColumnsAsync(Guid boardId, IReadOnlyList<Guid> orderedColumnIds)
    {
        if (orderedColumnIds.Count == 0) return;
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var tx = conn.BeginTransaction();
        var now = SqliteHelper.UtcNow();
        for (int i = 0; i < orderedColumnIds.Count; i++)
        {
            await using var cmd = new SqliteCommand(
                "UPDATE kanban_columns SET sort_order = @ord, updated_at = @now WHERE id = @id AND board_id = @bid",
                conn, tx);
            cmd.Parameters.AddWithValue("@ord", i);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id",  orderedColumnIds[i].ToString());
            cmd.Parameters.AddWithValue("@bid", boardId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task DeleteColumnAsync(Guid columnId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var now = SqliteHelper.UtcNow();
        await using var cardsCmd = new SqliteCommand(
            "UPDATE kanban_cards SET deleted_at = @now, updated_at = @now WHERE column_id = @cid AND deleted_at IS NULL",
            conn);
        cardsCmd.Parameters.AddWithValue("@now", now);
        cardsCmd.Parameters.AddWithValue("@cid", columnId.ToString());
        await cardsCmd.ExecuteNonQueryAsync();

        await using var colCmd = new SqliteCommand(
            "UPDATE kanban_columns SET deleted_at = @now, updated_at = @now WHERE id = @id", conn);
        colCmd.Parameters.AddWithValue("@now", now);
        colCmd.Parameters.AddWithValue("@id",  columnId.ToString());
        await colCmd.ExecuteNonQueryAsync();
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    public async Task<KanbanCard> AddCardAsync(Guid columnId, string title)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var id  = Guid.NewGuid().ToString();
        var now = SqliteHelper.UtcNow();
        await using var cmd = new SqliteCommand(
            @"INSERT INTO kanban_cards (id, column_id, title, sort_order, created_at, updated_at)
              VALUES (@id, @cid, @t,
                      (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM kanban_cards WHERE column_id = @cid),
                      @now, @now)",
            conn);
        cmd.Parameters.AddWithValue("@id",  id);
        cmd.Parameters.AddWithValue("@cid", columnId.ToString());
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();

        return new KanbanCard
        {
            Id        = Guid.Parse(id),
            ColumnId  = columnId,
            Title     = title,
            SortOrder = await GetCardSortOrderAsync(conn, id),
            CreatedAt = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task UpdateCardTitleAsync(Guid cardId, string title)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_cards SET title = @t, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@t",   title);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetCardNoteAsync(Guid cardId, Guid? noteId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_cards SET note_id = @nid, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@nid", noteId.HasValue ? noteId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCardAsync(Guid cardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_cards SET deleted_at = @now, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MoveCardToColumnAsync(Guid cardId, Guid columnId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_cards SET column_id = @cid, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@cid", columnId.ToString());
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenumberColumnCardsAsync(Guid columnId, IReadOnlyList<Guid> orderedCardIds)
    {
        if (orderedCardIds.Count == 0) return;
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var tx = conn.BeginTransaction();
        var now = SqliteHelper.UtcNow();
        for (int i = 0; i < orderedCardIds.Count; i++)
        {
            await using var cmd = new SqliteCommand(
                "UPDATE kanban_cards SET sort_order = @ord, updated_at = @now WHERE id = @id AND column_id = @cid",
                conn, tx);
            cmd.Parameters.AddWithValue("@ord", i);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id",  orderedCardIds[i].ToString());
            cmd.Parameters.AddWithValue("@cid", columnId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // ── Trash ─────────────────────────────────────────────────────────────────

    public async Task<IEnumerable<KanbanBoard>> GetTrashedBoardsAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT id, title, owner_id, created_at, updated_at, deleted_at FROM kanban_boards WHERE owner_id = @uid AND deleted_at IS NOT NULL ORDER BY deleted_at DESC",
            conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        var boards = new List<KanbanBoard>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            boards.Add(ReadBoard(r));
        return boards;
    }

    public async Task<IEnumerable<KanbanColumn>> GetTrashedColumnsAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            @"SELECT c.id, c.board_id, c.title, c.sort_order, c.deleted_at
              FROM kanban_columns c
              JOIN kanban_boards b ON b.id = c.board_id
              WHERE c.deleted_at IS NOT NULL
                AND b.deleted_at IS NULL
                AND b.owner_id = @uid
              ORDER BY c.deleted_at DESC",
            conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        var cols = new List<KanbanColumn>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            cols.Add(new KanbanColumn
            {
                Id        = r.GetGuid(0),
                BoardId   = r.GetGuid(1),
                Title     = r.GetString(2),
                SortOrder = r.GetInt32(3),
                DeletedAt = r.GetNullableDateTime(4)
            });
        return cols;
    }

    public async Task<IEnumerable<KanbanCard>> GetTrashedCardsAsync(Guid userId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            @"SELECT k.id, k.column_id, k.title, k.note_id, k.sort_order, k.created_at, k.deleted_at
              FROM kanban_cards k
              JOIN kanban_columns c ON c.id = k.column_id
              JOIN kanban_boards  b ON b.id = c.board_id
              WHERE k.deleted_at IS NOT NULL
                AND c.deleted_at IS NULL
                AND b.deleted_at IS NULL
                AND b.owner_id = @uid
              ORDER BY k.deleted_at DESC",
            conn);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        var cards = new List<KanbanCard>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            cards.Add(new KanbanCard
            {
                Id        = r.GetGuid(0),
                ColumnId  = r.GetGuid(1),
                Title     = r.GetString(2),
                NoteId    = r.GetNullableGuid(3),
                SortOrder = r.GetInt32(4),
                CreatedAt = r.GetDateTime(5),
                DeletedAt = r.GetNullableDateTime(6)
            });
        return cards;
    }

    public async Task RestoreBoardAsync(Guid boardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var now = SqliteHelper.UtcNow();
        await using var cardsCmd = new SqliteCommand(
            @"UPDATE kanban_cards SET deleted_at = NULL, updated_at = @now
              WHERE column_id IN (SELECT id FROM kanban_columns WHERE board_id = @bid)
                AND deleted_at IS NOT NULL",
            conn);
        cardsCmd.Parameters.AddWithValue("@now", now);
        cardsCmd.Parameters.AddWithValue("@bid", boardId.ToString());
        await cardsCmd.ExecuteNonQueryAsync();

        await using var colsCmd = new SqliteCommand(
            "UPDATE kanban_columns SET deleted_at = NULL, updated_at = @now WHERE board_id = @bid", conn);
        colsCmd.Parameters.AddWithValue("@now", now);
        colsCmd.Parameters.AddWithValue("@bid", boardId.ToString());
        await colsCmd.ExecuteNonQueryAsync();

        await using var boardCmd = new SqliteCommand(
            "UPDATE kanban_boards SET deleted_at = NULL, updated_at = @now WHERE id = @id", conn);
        boardCmd.Parameters.AddWithValue("@now", now);
        boardCmd.Parameters.AddWithValue("@id",  boardId.ToString());
        await boardCmd.ExecuteNonQueryAsync();
    }

    public async Task RestoreColumnAsync(Guid columnId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        var now = SqliteHelper.UtcNow();
        await using var cardsCmd = new SqliteCommand(
            "UPDATE kanban_cards SET deleted_at = NULL, updated_at = @now WHERE column_id = @cid AND deleted_at IS NOT NULL",
            conn);
        cardsCmd.Parameters.AddWithValue("@now", now);
        cardsCmd.Parameters.AddWithValue("@cid", columnId.ToString());
        await cardsCmd.ExecuteNonQueryAsync();

        await using var colCmd = new SqliteCommand(
            "UPDATE kanban_columns SET deleted_at = NULL, updated_at = @now WHERE id = @id", conn);
        colCmd.Parameters.AddWithValue("@now", now);
        colCmd.Parameters.AddWithValue("@id",  columnId.ToString());
        await colCmd.ExecuteNonQueryAsync();
    }

    public async Task RestoreCardAsync(Guid cardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "UPDATE kanban_cards SET deleted_at = NULL, updated_at = @now WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@now", SqliteHelper.UtcNow());
        cmd.Parameters.AddWithValue("@id",  cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteBoardAsync(Guid boardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand("DELETE FROM kanban_boards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", boardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteColumnAsync(Guid columnId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand("DELETE FROM kanban_columns WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", columnId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PermanentlyDeleteCardAsync(Guid cardId)
    {
        await using var conn = SqliteHelper.Open(dbPath);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand("DELETE FROM kanban_cards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", cardId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<int> GetCardSortOrderAsync(SqliteConnection conn, string id)
    {
        await using var cmd = new SqliteCommand(
            "SELECT sort_order FROM kanban_cards WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        var v = await cmd.ExecuteScalarAsync();
        return v is long l ? (int)l : 0;
    }

    private static KanbanBoard ReadBoard(SqliteDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        Title     = r.GetString(1),
        OwnerId   = r.GetGuid(2),
        CreatedAt = r.GetDateTime(3),
        UpdatedAt = r.GetDateTime(4),
        DeletedAt = r.GetNullableDateTime(5)
    };
}
