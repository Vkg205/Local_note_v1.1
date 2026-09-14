namespace LocalNote.Storage.Services;

public static class DiskSpaceGuard
{
    private const long DefaultReserveBytes = 64L * 1024 * 1024;

    public static void EnsureWritableSpace(string targetPath, long expectedWriteBytes = 0, long reserveBytes = DefaultReserveBytes)
    {
        var full = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;
            var required = Math.Max(0, expectedWriteBytes) + Math.Max(0, reserveBytes);
            if (drive.AvailableFreeSpace < required)
            {
                throw new IOException($"磁盘剩余空间不足。当前可用 {FormatBytes(drive.AvailableFreeSpace)}，本次操作至少需要 {FormatBytes(required)}（含安全余量）。");
            }
        }
        catch (ArgumentException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string GetFreeSpaceSummary(string targetPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrWhiteSpace(root)) return "未知";
            var drive = new DriveInfo(root);
            return drive.IsReady ? FormatBytes(drive.AvailableFreeSpace) : "未知";
        }
        catch { return "未知"; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
