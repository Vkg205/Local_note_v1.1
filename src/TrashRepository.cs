using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class TrashRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<TrashEntry>> GetAsync()
    {
        var items = new List<TrashEntry>();
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,'页面',p.title,p.updated_at
              FROM pages p JOIN sections s ON s.id=p.section_id JOIN notebooks n ON n.id=s.notebook_id
             WHERE p.is_deleted=1 AND s.is_deleted=0 AND n.is_deleted=0
            UNION ALL
            SELECT s.id,'分区',s.name,s.updated_at
              FROM sections s JOIN notebooks n ON n.id=s.notebook_id
             WHERE s.is_deleted=1 AND n.is_deleted=0
            UNION ALL
            SELECT id,'笔记本',name,updated_at FROM notebooks WHERE is_deleted=1
            ORDER BY updated_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(new TrashEntry { Id = reader.GetString(0), Kind = reader.GetString(1), Name = reader.GetString(2), UpdatedAt = DateTimeOffset.Parse(reader.GetString(3)) });
        return items;
    }

    public async Task RestoreAsync(TrashEntry item)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        switch (item.Kind)
        {
            case "笔记本":
                await ExecuteAsync(connection, transaction, "UPDATE notebooks SET is_deleted=0,updated_at=$now WHERE id=$id;", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE sections SET is_deleted=0,updated_at=$now WHERE notebook_id=$id;", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE pages SET is_deleted=0,updated_at=$now WHERE section_id IN (SELECT id FROM sections WHERE notebook_id=$id);", item.Id, now);
                break;
            case "分区":
                await ExecuteAsync(connection, transaction, "UPDATE notebooks SET is_deleted=0,updated_at=$now WHERE id=(SELECT notebook_id FROM sections WHERE id=$id);", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE sections SET is_deleted=0,updated_at=$now WHERE id=$id;", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE pages SET is_deleted=0,updated_at=$now WHERE section_id=$id;", item.Id, now);
                break;
            case "页面":
                await ExecuteAsync(connection, transaction, "UPDATE notebooks SET is_deleted=0,updated_at=$now WHERE id=(SELECT s.notebook_id FROM pages p JOIN sections s ON s.id=p.section_id WHERE p.id=$id);", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE sections SET is_deleted=0,updated_at=$now WHERE id=(SELECT section_id FROM pages WHERE id=$id);", item.Id, now);
                await ExecuteAsync(connection, transaction, "UPDATE pages SET is_deleted=0,updated_at=$now WHERE id=$id;", item.Id, now);
                break;
            default: throw new InvalidOperationException("未知回收站对象类型。");
        }
        await transaction.CommitAsync();
    }

    public async Task EmptyAsync()
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var statements = new[]
        {
            "DELETE FROM search_fts WHERE page_id IN (SELECT p.id FROM pages p LEFT JOIN sections s ON s.id=p.section_id LEFT JOIN notebooks n ON n.id=s.notebook_id WHERE p.is_deleted=1 OR s.is_deleted=1 OR n.is_deleted=1);",
            "DELETE FROM content_objects WHERE page_id IN (SELECT p.id FROM pages p LEFT JOIN sections s ON s.id=p.section_id LEFT JOIN notebooks n ON n.id=s.notebook_id WHERE p.is_deleted=1 OR s.is_deleted=1 OR n.is_deleted=1);",
            "DELETE FROM ink_layers WHERE page_id IN (SELECT p.id FROM pages p LEFT JOIN sections s ON s.id=p.section_id LEFT JOIN notebooks n ON n.id=s.notebook_id WHERE p.is_deleted=1 OR s.is_deleted=1 OR n.is_deleted=1);",
            "DELETE FROM pages WHERE is_deleted=1 OR section_id IN (SELECT s.id FROM sections s LEFT JOIN notebooks n ON n.id=s.notebook_id WHERE s.is_deleted=1 OR n.is_deleted=1);",
            "DELETE FROM sections WHERE is_deleted=1 OR notebook_id IN (SELECT id FROM notebooks WHERE is_deleted=1);",
            "DELETE FROM notebooks WHERE is_deleted=1;"
        };
        foreach (var sql in statements)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, string id, string now)
    {
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", now); await command.ExecuteNonQueryAsync();
    }
}
