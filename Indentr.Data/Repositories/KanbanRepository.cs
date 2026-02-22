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
            "RETURNING id, title, owner_id, created_at, updated_at", conn);
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
            "SELECT id, title, owner_id, created_at, updated_at FROM kanban_boards WHERE id = @id", conn);
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
            LEFT   JOIN kanban_cards k ON k.column_id = c.id
            WHERE  c.board_id = @bid
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
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM kanban_columns WHERE id = @id", conn);
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
            "DELETE FROM kanban_cards WHERE id = @id", conn);
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
        for (int i = 0; i < orderedCardIds.Count; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE kanban_cards SET sort_order = @ord WHERE id = @id AND column_id = @cid", conn);
            cmd.Parameters.AddWithValue("ord", i);
            cmd.Parameters.AddWithValue("id", orderedCardIds[i]);
            cmd.Parameters.AddWithValue("cid", columnId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KanbanBoard ReadBoard(NpgsqlDataReader r) => new()
    {
        Id        = r.GetGuid(0),
        Title     = r.GetString(1),
        OwnerId   = r.GetGuid(2),
        CreatedAt = r.GetDateTime(3),
        UpdatedAt = r.GetDateTime(4)
    };
}
