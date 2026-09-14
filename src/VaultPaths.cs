namespace LocalNote.Storage.Services;

public sealed class VaultPaths
{
    public VaultPaths(string rootPath) => Root = Path.GetFullPath(rootPath);
    public string Root { get; }
    public string Manifest => Path.Combine(Root, "vault.json");
    public string Database => Path.Combine(Root, "notes.db");
    public string LockFile => Path.Combine(Root, ".localnote.lock");
    public string Media => Path.Combine(Root, "media");
    public string Images => Path.Combine(Media, "images");
    public string Attachments => Path.Combine(Media, "attachments");
    public string Audio => Path.Combine(Media, "audio");
    public string Video => Path.Combine(Media, "video");
    public string Thumbnails => Path.Combine(Root, "thumbnails");
    public string Temp => Path.Combine(Root, "temp");
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Media); Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Attachments); Directory.CreateDirectory(Audio); Directory.CreateDirectory(Video);
        Directory.CreateDirectory(Thumbnails); Directory.CreateDirectory(Temp); Directory.CreateDirectory(Backups); Directory.CreateDirectory(Logs);
    }
}
