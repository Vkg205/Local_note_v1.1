using System.Text.Json;
using LocalNote.Domain.Entities;

namespace LocalNote.Storage.Services;

public sealed class VaultService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<(VaultInfo Vault, SqliteDataStore Store)> CreateAsync(
        string rootPath,
        string name,
        CancellationToken cancellationToken = default)
    {
        var paths = new VaultPaths(rootPath);
        paths.EnsureDirectories();

        if (File.Exists(paths.Manifest))
            throw new InvalidOperationException("所选目录已经包含 LocalNote 仓库。请直接打开该仓库。 ");

        var vault = new VaultInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "我的笔记" : name.Trim(),
            RootPath = paths.Root,
            SchemaVersion = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var manifestJson = JsonSerializer.Serialize(vault, JsonOptions);
        await File.WriteAllTextAsync(paths.Manifest, manifestJson, cancellationToken);

        var store = new SqliteDataStore(paths.Database);
        await store.InitializeAsync(cancellationToken);
        return (vault, store);
    }

    public async Task<(VaultInfo Vault, SqliteDataStore Store)> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var paths = new VaultPaths(rootPath);
        if (!File.Exists(paths.Manifest))
            throw new FileNotFoundException("该目录不是有效的 LocalNote 仓库：缺少 vault.json。", paths.Manifest);

        var json = await File.ReadAllTextAsync(paths.Manifest, cancellationToken);
        var vault = JsonSerializer.Deserialize<VaultInfo>(json, JsonOptions)
                    ?? throw new InvalidDataException("vault.json 无法解析。 ");

        var normalized = new VaultInfo
        {
            Id = vault.Id,
            Name = vault.Name,
            RootPath = paths.Root,
            SchemaVersion = vault.SchemaVersion,
            CreatedAt = vault.CreatedAt,
            UpdatedAt = vault.UpdatedAt
        };

        paths.EnsureDirectories();
        var store = new SqliteDataStore(paths.Database);
        await store.InitializeAsync(cancellationToken);
        return (normalized, store);
    }
}
