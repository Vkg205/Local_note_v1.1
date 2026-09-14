namespace LocalNote.Storage.Services;

public sealed class SessionStateService(SqliteDataStore store)
{
    public async Task<bool> BeginAsync()
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var read = connection.CreateCommand(); read.CommandText = "SELECT value FROM app_meta WHERE key='clean_shutdown';";
        var previous = Convert.ToString(await read.ExecuteScalarAsync()) ?? "1";
        var write = connection.CreateCommand();
        write.CommandText = "INSERT INTO app_meta(key,value) VALUES('clean_shutdown','0') ON CONFLICT(key) DO UPDATE SET value='0';";
        await write.ExecuteNonQueryAsync();
        return previous == "0";
    }

    public async Task EndAsync()
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_meta(key,value) VALUES('clean_shutdown','1') ON CONFLICT(key) DO UPDATE SET value='1';";
        await command.ExecuteNonQueryAsync();
        await store.CheckpointAsync();
    }
}
