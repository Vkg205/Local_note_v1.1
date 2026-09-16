using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class ContentObjectRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<ContentObject>> GetByPageAsync(string pageId)
    {
        var items = new List<ContentObject>();
        await using var connection = store.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,page_id,type,x,y,width,height,z_index,payload,style_json,is_todo,todo_completed,is_important,
                   local_version,created_at,updated_at
            FROM content_objects WHERE page_id=$page ORDER BY z_index, created_at;
            """;
        command.Parameters.AddWithValue("$page", pageId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new ContentObject
            {
                Id = reader.GetString(0), PageId = reader.GetString(1), Type = Enum.Parse<ContentObjectType>(reader.GetString(2), true),
                X = reader.GetDouble(3), Y = reader.GetDouble(4), Width = reader.GetDouble(5), Height = reader.GetDouble(6),
                ZIndex = reader.GetInt32(7), Payload = reader.GetString(8), StyleJson = reader.GetString(9),
                IsTodo = reader.GetInt32(10) != 0, TodoCompleted = reader.GetInt32(11) != 0, IsImportant = reader.GetInt32(12) != 0,
                LocalVersion = reader.GetInt32(13), CreatedAt = DateTimeOffset.Parse(reader.GetString(14)), UpdatedAt = DateTimeOffset.Parse(reader.GetString(15))
            });
        }
        return items;
    }

    public async Task<ContentObject> CreateAsync(string pageId, ContentObjectType type, double x, double y, double width, double height, string payload, string? searchText = null)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ContentObject
        {
            Id = Guid.NewGuid().ToString("N"), PageId = pageId, Type = type, X = x, Y = y,
            Width = width, Height = height, ZIndex = (int)(now.ToUnixTimeMilliseconds() % int.MaxValue), Payload = payload,
            SearchText = searchText ?? string.Empty, CreatedAt = now, UpdatedAt = now
        };
        await UpsertAsync(item);
        return item;
    }

    public async Task UpsertAsync(ContentObject item)
    {
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.LocalVersion++;
        await using var connection = store.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO content_objects(id,page_id,type,x,y,width,height,z_index,payload,style_json,is_todo,todo_completed,is_important,
                                        local_version,created_at,updated_at)
            VALUES($id,$page,$type,$x,$y,$w,$h,$z,$payload,$style,$todo,$done,$important,$version,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET x=$x,y=$y,width=$w,height=$h,z_index=$z,payload=$payload,style_json=$style,
                is_todo=$todo,todo_completed=$done,is_important=$important,local_version=$version,updated_at=$updated;
            """;
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$page", item.PageId);
        command.Parameters.AddWithValue("$type", item.Type.ToString()); command.Parameters.AddWithValue("$x", item.X);
        command.Parameters.AddWithValue("$y", item.Y); command.Parameters.AddWithValue("$w", item.Width);
        command.Parameters.AddWithValue("$h", item.Height); command.Parameters.AddWithValue("$z", item.ZIndex);
        command.Parameters.AddWithValue("$payload", item.Payload ?? string.Empty); command.Parameters.AddWithValue("$style", item.StyleJson ?? "{}");
        command.Parameters.AddWithValue("$todo", item.IsTodo ? 1 : 0); command.Parameters.AddWithValue("$done", item.TodoCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$important", item.IsImportant ? 1 : 0); command.Parameters.AddWithValue("$version", item.LocalVersion);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O")); command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();

        var deleteIndex = connection.CreateCommand(); deleteIndex.Transaction = (SqliteTransaction)transaction;
        deleteIndex.CommandText = "DELETE FROM search_fts WHERE object_id=$id;"; deleteIndex.Parameters.AddWithValue("$id", item.Id);
        await deleteIndex.ExecuteNonQueryAsync();
        var searchable = ResolveSearchText(item);
        if (!string.IsNullOrWhiteSpace(searchable))
        {
            var index = connection.CreateCommand(); index.Transaction = (SqliteTransaction)transaction;
            index.CommandText = "INSERT INTO search_fts(object_id,page_id,source,text) VALUES($id,$page,$source,$text);";
            index.Parameters.AddWithValue("$id", item.Id); index.Parameters.AddWithValue("$page", item.PageId);
            index.Parameters.AddWithValue("$source", item.Type == ContentObjectType.Table ? "table" : "text");
            index.Parameters.AddWithValue("$text", searchable);
            await index.ExecuteNonQueryAsync();
        }
        var touchPage = connection.CreateCommand(); touchPage.Transaction = (SqliteTransaction)transaction;
        touchPage.CommandText = "UPDATE pages SET updated_at=$updated,local_version=local_version+1 WHERE id=$page;";
        touchPage.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O")); touchPage.Parameters.AddWithValue("$page", item.PageId);
        await touchPage.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM content_objects WHERE id=$id;"; command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        var search = connection.CreateCommand(); search.Transaction = (SqliteTransaction)transaction;
        search.CommandText = "DELETE FROM search_fts WHERE object_id=$id;"; search.Parameters.AddWithValue("$id", id);
        await search.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }


    public async Task DeleteAllForPageAsync(string pageId)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var search = connection.CreateCommand(); search.Transaction = (SqliteTransaction)transaction;
        search.CommandText = "DELETE FROM search_fts WHERE page_id=$page;"; search.Parameters.AddWithValue("$page", pageId);
        await search.ExecuteNonQueryAsync();
        var objects = connection.CreateCommand(); objects.Transaction = (SqliteTransaction)transaction;
        objects.CommandText = "DELETE FROM content_objects WHERE page_id=$page;"; objects.Parameters.AddWithValue("$page", pageId);
        await objects.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static string ResolveSearchText(ContentObject item)
    {
        if (!string.IsNullOrWhiteSpace(item.SearchText)) return item.SearchText.Trim();
        if (item.Type == ContentObjectType.Text && !item.Payload.TrimStart().StartsWith("<FlowDocument", StringComparison.Ordinal)) return item.Payload;
        return string.Empty;
    }
}
