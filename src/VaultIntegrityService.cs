using System.Text.Json;

namespace LocalNote.Storage.Services;

public sealed class VaultIntegrityService(string vaultRoot, SqliteDataStore store)
{
    public async Task<string> CheckAsync()
    {
        var lines = new List<string>();
        await using var connection = store.CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check;";
        var dbResult = Convert.ToString(await command.ExecuteScalarAsync()) ?? "unknown";
        lines.Add($"SQLite integrity_check: {dbResult}");

        var media = connection.CreateCommand();
        media.CommandText = "SELECT type,payload FROM content_objects WHERE type IN ('Image','Attachment');";
        await using var reader = await media.ExecuteReaderAsync();
        var checkedCount = 0; var missing = 0;
        while (await reader.ReadAsync())
        {
            checkedCount++;
            var json = reader.GetString(1);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("RelativePath", out var pathProp)) continue;
                var relative = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var absolute = Path.Combine(vaultRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute)) { missing++; lines.Add($"缺失媒体: {relative}"); }
            }
            catch { lines.Add("发现无法解析的媒体对象 payload。"); missing++; }
        }
        lines.Add($"媒体引用检查: {checkedCount} 项，缺失 {missing} 项");
        lines.Add(dbResult.Equals("ok", StringComparison.OrdinalIgnoreCase) && missing == 0 ? "结论: PASS" : "结论: CHECK REQUIRED");
        return string.Join(Environment.NewLine, lines);
    }
}
