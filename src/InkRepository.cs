using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class InkRepository(SqliteDataStore store)
{
    public async Task<byte[]?> GetAsync(string pageId)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = "SELECT isf_data FROM ink_layers WHERE page_id=$page;"; command.Parameters.AddWithValue("$page", pageId);
        var result = await command.ExecuteScalarAsync(); return result is null or DBNull ? null : (byte[])result;
    }

    public async Task SaveAsync(string pageId, byte[] data)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO ink_layers(page_id,isf_data,updated_at) VALUES($page,$data,$updated)
            ON CONFLICT(page_id) DO UPDATE SET isf_data=$data,updated_at=$updated;
            """;
        command.Parameters.AddWithValue("$page", pageId); command.Parameters.AddWithValue("$data", data); command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync();
        var touch = connection.CreateCommand(); touch.Transaction = (SqliteTransaction)transaction;
        touch.CommandText = "UPDATE pages SET updated_at=$updated,local_version=local_version+1 WHERE id=$page;"; touch.Parameters.AddWithValue("$updated", now); touch.Parameters.AddWithValue("$page", pageId);
        await touch.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
}
