namespace LocalNote.Storage.Services;

public sealed class VaultWriteLock : IDisposable
{
    private readonly FileStream _stream;
    private VaultWriteLock(FileStream stream) => _stream = stream;

    public static VaultWriteLock Acquire(string vaultRoot)
    {
        var path = Path.Combine(Path.GetFullPath(vaultRoot), ".localnote.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose);
            stream.Lock(0, 1);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            writer.Write($"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}"); writer.Flush();
            stream.Position = 0;
            return new VaultWriteLock(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("该仓库正在被另一个 LocalNote 进程写入。为避免数据覆盖，本次打开已被阻止。请关闭其他 LocalNote 窗口后重试。", ex);
        }
    }

    public void Dispose()
    {
        try { _stream.Unlock(0, 1); } catch { }
        _stream.Dispose();
    }
}
