using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class SearchRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? notebookId = null, string? sectionId = null, string? pageId = null, int limit = 50)
    {
        var items = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return items;
        await using var connection = store.CreateConnection(); await connection.OpenAsync();

        var scopeSql = BuildScopeSql(notebookId, sectionId, pageId);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id,NULL,p.title,'title',p.title,p.updated_at AS updated_at,0 AS rank
              FROM pages p JOIN sections s ON s.id=p.section_id
             WHERE p.is_deleted=0 AND s.is_deleted=0 {scopeSql} AND p.title LIKE $like
            UNION ALL
            SELECT p.id,f.object_id,p.title,f.source,
                   CASE WHEN length(f.text)>180 THEN substr(f.text,1,180)||'…' ELSE f.text END,
                   p.updated_at AS updated_at, bm25(search_fts) AS rank
              FROM search_fts f
              JOIN pages p ON p.id=f.page_id
              JOIN sections s ON s.id=p.section_id
             WHERE p.is_deleted=0 AND s.is_deleted=0 {scopeSql} AND search_fts MATCH $match
            ORDER BY rank, updated_at DESC LIMIT $limit;
            """;
        AddScopeParameters(command, notebookId, sectionId, pageId);
        command.Parameters.AddWithValue("$like", $"%{query.Trim()}%");
        command.Parameters.AddWithValue("$match", ToFtsQuery(query));
        command.Parameters.AddWithValue("$limit", limit);
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new SearchHit
                {
                    PageId = reader.GetString(0), ObjectId = reader.IsDBNull(1) ? null : reader.GetString(1), PageTitle = reader.GetString(2),
                    Source = reader.GetString(3), Snippet = reader.GetString(4), UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
                });
            }
            return items;
        }
        catch (SqliteException)
        {
            return await SearchFallbackAsync(query, notebookId, sectionId, pageId, limit);
        }
    }

    private async Task<IReadOnlyList<SearchHit>> SearchFallbackAsync(string query, string? notebookId, string? sectionId, string? pageId, int limit)
    {
        var items = new List<SearchHit>();
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var scopeSql = BuildScopeSql(notebookId, sectionId, pageId);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id,NULL,p.title,'title',p.title,p.updated_at AS updated_at
              FROM pages p JOIN sections s ON s.id=p.section_id
             WHERE p.is_deleted=0 AND s.is_deleted=0 {scopeSql} AND p.title LIKE $like
            UNION ALL
            SELECT p.id,f.object_id,p.title,f.source,substr(f.text,1,180),p.updated_at AS updated_at
              FROM search_fts f JOIN pages p ON p.id=f.page_id JOIN sections s ON s.id=p.section_id
             WHERE p.is_deleted=0 AND s.is_deleted=0 {scopeSql} AND f.text LIKE $like
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        AddScopeParameters(command, notebookId, sectionId, pageId);
        command.Parameters.AddWithValue("$like", $"%{query.Trim()}%"); command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new SearchHit
            {
                PageId = reader.GetString(0), ObjectId = reader.IsDBNull(1) ? null : reader.GetString(1), PageTitle = reader.GetString(2),
                Source = reader.GetString(3), Snippet = reader.GetString(4), UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
            });
        }
        return items;
    }

    private static string BuildScopeSql(string? notebookId, string? sectionId, string? pageId)
    {
        if (!string.IsNullOrWhiteSpace(pageId)) return " AND p.id=$page ";
        if (!string.IsNullOrWhiteSpace(sectionId)) return " AND p.section_id=$section ";
        if (!string.IsNullOrWhiteSpace(notebookId)) return " AND s.notebook_id=$notebook ";
        return string.Empty;
    }

    private static void AddScopeParameters(SqliteCommand command, string? notebookId, string? sectionId, string? pageId)
    {
        if (!string.IsNullOrWhiteSpace(pageId)) command.Parameters.AddWithValue("$page", pageId);
        else if (!string.IsNullOrWhiteSpace(sectionId)) command.Parameters.AddWithValue("$section", sectionId);
        else if (!string.IsNullOrWhiteSpace(notebookId)) command.Parameters.AddWithValue("$notebook", notebookId);
    }

    private static string ToFtsQuery(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return "\"\"";
        return string.Join(" AND ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));
    }
}
