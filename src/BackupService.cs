using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalNote.Storage.Services;

public sealed class BackupService(string vaultRoot, SqliteDataStore store)
{
    private sealed record ManifestFile(string Path, long Size, string Sha256);
    private sealed record BackupManifest(string Format, int Version, DateTimeOffset CreatedAt, string Source, List<ManifestFile>? Files);

    public async Task CreateAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        var outputFullPath = Path.GetFullPath(outputPath);
        var estimated = Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories)
            .Where(file => ShouldIncludeSourceFile(file, outputFullPath, vaultRoot))
            .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        DiskSpaceGuard.EnsureWritableSpace(outputPath, estimated);
        await store.CheckpointAsync(cancellationToken);
        var tempRoot = Path.Combine(Path.GetTempPath(), "LocalNoteBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var files = new List<ManifestFile>();
            foreach (var file in Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldIncludeSourceFile(file, outputFullPath, vaultRoot)) continue;
                var relative = Path.GetRelativePath(vaultRoot, file);
                var target = Path.Combine(tempRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                files.Add(new ManifestFile(relative.Replace('\\','/'), new FileInfo(target).Length, await HashAsync(target, cancellationToken)));
            }
            var manifest = new BackupManifest("localnote-backup", 2, DateTimeOffset.UtcNow, Path.GetFileName(vaultRoot), files);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "backup-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempRoot, outputPath, CompressionLevel.Optimal, false);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    public static async Task RestoreAsync(string archivePath, string destinationRoot, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
            throw new InvalidOperationException("恢复目标目录必须为空。请新建一个空目录后再恢复。");
        Directory.CreateDirectory(destinationRoot);
        var tempRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destinationRoot))!, ".localnote-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, tempRoot, false);
            var manifestPath = Path.Combine(tempRoot, "backup-manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("不是有效的 LocalNote 备份包：缺少 backup-manifest.json。");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidDataException("备份清单无法解析。");
            if (manifest.Format != "localnote-backup" || manifest.Version is < 1 or > 2) throw new InvalidDataException("备份格式或版本不兼容。");
            if (manifest.Version >= 2)
            {
                foreach (var entry in manifest.Files ?? [])
                {
                    var safeRelative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                    var full = Path.GetFullPath(Path.Combine(tempRoot, safeRelative));
                    if (!full.StartsWith(Path.GetFullPath(tempRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                        throw new InvalidDataException($"备份文件缺失：{entry.Path}");
                    var info = new FileInfo(full);
                    if (info.Length != entry.Size || !string.Equals(await HashAsync(full, cancellationToken), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"备份完整性校验失败：{entry.Path}");
                }
            }
            File.Delete(manifestPath);
            if (!File.Exists(Path.Combine(tempRoot, "notes.db"))) throw new InvalidDataException("备份包缺少 notes.db。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(tempRoot))
            {
                var target = Path.Combine(destinationRoot, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target);
            }
            var store = new SqliteDataStore(Path.Combine(destinationRoot, "notes.db"));
            await store.InitializeAsync(cancellationToken);
        }
        catch
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(destinationRoot))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
                }
            }
            catch { }
            throw;
        }
        finally { try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { } }
    }

    private static bool ShouldIncludeSourceFile(string file, string outputFullPath, string sourceRoot)
    {
        var full = Path.GetFullPath(file);
        if (string.Equals(full, outputFullPath, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(sourceRoot, full).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (relative.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (relative.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Path.GetFileName(relative), ".localnote.lock", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
