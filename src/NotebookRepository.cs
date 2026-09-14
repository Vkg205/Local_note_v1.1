using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class NotebookRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<Notebook>> GetActiveAsync(string vaultId, CancellationToken cancellationToken = default)
    {
        var result = new List<Notebook>();
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, vault_id, name, color, sort_order, is_deleted, created_at, updated_at
            FROM notebooks
            WHERE vault_id = $vaultId AND is_deleted = 0
            ORDER BY sort_order, created_at;
            """;
        command.Parameters.AddWithValue("$vaultId", vaultId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    public async Task<Notebook> CreateAsync(string vaultId, string name, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new Notebook
        {
            Id = Guid.NewGuid().ToString("N"),
            VaultId = vaultId,
            Name = name,
            Color = "#2B579A",
            SortOrder = await GetNextSortOrderAsync(vaultId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notebooks(id, vault_id, name, color, sort_order, is_deleted, created_at, updated_at)
            VALUES($id, $vaultId, $name, $color, $sortOrder, 0, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$vaultId", item.VaultId);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$color", item.Color);
        command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("UPDATE notebooks SET name=$name, updated_at=$now WHERE id=$id;", id, name, cancellationToken);
    }

    public async Task SoftDeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var pages = connection.CreateCommand();
        pages.Transaction = transaction;
        pages.CommandText = """
            UPDATE pages SET is_deleted=1, updated_at=$now
            WHERE section_id IN (SELECT id FROM sections WHERE notebook_id=$id);
            """;
        pages.Parameters.AddWithValue("$id", id);
        pages.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await pages.ExecuteNonQueryAsync(cancellationToken);

        var sections = connection.CreateCommand();
        sections.Transaction = transaction;
        sections.CommandText = "UPDATE sections SET is_deleted=1, updated_at=$now WHERE notebook_id=$id;";
        sections.Parameters.AddWithValue("$id", id);
        sections.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await sections.ExecuteNonQueryAsync(cancellationToken);

        var notebook = connection.CreateCommand();
        notebook.Transaction = transaction;
        notebook.CommandText = "UPDATE notebooks SET is_deleted=1, updated_at=$now WHERE id=$id;";
        notebook.Parameters.AddWithValue("$id", id);
        notebook.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await notebook.ExecuteNonQueryAsync(cancellationToken);

        transaction.Commit();
    }

    private async Task ExecuteAsync(string sql, string id, string name, CancellationToken cancellationToken)
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

    private async Task<int> GetNextSortOrderAsync(string vaultId, CancellationToken cancellationToken)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM notebooks WHERE vault_id=$vaultId;";
        command.Parameters.AddWithValue("$vaultId", vaultId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Notebook Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        VaultId = reader.GetString(1),
        Name = reader.GetString(2),
        Color = reader.GetString(3),
        SortOrder = reader.GetInt32(4),
        IsDeleted = reader.GetInt32(5) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
    };
}
