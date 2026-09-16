using System.Security.Cryptography;

namespace LocalNote.Storage.Services;

public sealed class MediaStoreService(string vaultRoot)
{
    public async Task<string> ImportAsync(string sourcePath, string category, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("文件不存在", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        DiskSpaceGuard.EnsureWritableSpace(vaultRoot, sourceInfo.Length);
        await using var input = File.OpenRead(sourcePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var relative = Path.Combine("media", category, hash[..2], hash + ext);
        var destination = Path.Combine(vaultRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            input.Position = 0;
            var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = File.Create(temp)) await input.CopyToAsync(output, cancellationToken);
                File.Move(temp, destination, false);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        return relative.Replace('\\', '/');
    }

    public string GetAbsolutePath(string relativePath) => Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
