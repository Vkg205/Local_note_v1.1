using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class SectionRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<Section>> GetActiveAsync(string notebookId, CancellationToken cancellationToken = default)
    {
        var result = new List<Section>();
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, notebook_id, parent_group_id, name, color, sort_order, is_deleted, created_at, updated_at
            FROM sections
            WHERE notebook_id=$notebookId AND is_deleted=0
            ORDER BY sort_order, created_at;
            """;
        command.Parameters.AddWithValue("$notebookId", notebookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<Section> CreateAsync(string notebookId, string name, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sortOrder = await GetNextSortOrderAsync(notebookId, cancellationToken);
        string[] palette = ["#6C2AA5", "#2563EB", "#0F9D8B", "#D97706", "#D14343", "#C026D3", "#4F46E5"];
        var item = new Section
        {
            Id = Guid.NewGuid().ToString("N"),
            NotebookId = notebookId,
            Name = name,
            Color = palette[sortOrder % palette.Length],
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sections(id, notebook_id, parent_group_id, name, color, sort_order, is_deleted, created_at, updated_at)
            VALUES($id, $notebookId, NULL, $name, $color, $sortOrder, 0, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$notebookId", item.NotebookId);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$color", item.Color);
        command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        await UpdateAsync("UPDATE sections SET name=$name, updated_at=$now WHERE id=$id;", id, name, cancellationToken);


    public async Task UpdateColorAsync(string id, string color, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE sections SET color=$color, updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SoftDeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var pages = connection.CreateCommand();
        pages.Transaction = transaction;
        pages.CommandText = "UPDATE pages SET is_deleted=1, updated_at=$now WHERE section_id=$id;";
        pages.Parameters.AddWithValue("$id", id);
        pages.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await pages.ExecuteNonQueryAsync(cancellationToken);
        var section = connection.CreateCommand();
        section.Transaction = transaction;
        section.CommandText = "UPDATE sections SET is_deleted=1, updated_at=$now WHERE id=$id;";
        section.Parameters.AddWithValue("$id", id);
        section.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await section.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    private async Task UpdateAsync(string sql, string id, string name, CancellationToken cancellationToken)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> GetNextSortOrderAsync(string notebookId, CancellationToken cancellationToken)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM sections WHERE notebook_id=$notebookId;";
        command.Parameters.AddWithValue("$notebookId", notebookId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Section Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        NotebookId = reader.GetString(1),
        ParentGroupId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Name = reader.GetString(3),
        Color = reader.GetString(4),
        SortOrder = reader.GetInt32(5),
        IsDeleted = reader.GetInt32(6) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(8))
    };
}
