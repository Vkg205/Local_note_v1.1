using System.IO;
using System.Text.Json;

namespace LocalNote.App.Services;

public enum RibbonDisplayMode
{
    Expanded,
    Compact,
    Collapsed
}

public sealed record NavigationState(string? VaultPath, string? NotebookId, string? SectionId, string? PageId);

public sealed class UserSettingsService
{
    private readonly string _settingsFile;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // MainWindow and MainViewModel each keep a UserSettingsService instance.  A
    // process-wide gate is therefore required so read-modify-write operations do
    // not overwrite each other when layout/navigation state is saved at the same
    // time.  The gate intentionally covers BOTH reading and writing.
    private static readonly SemaphoreSlim SettingsGate = new(1, 1);

    public UserSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalNote");
        Directory.CreateDirectory(dir);
        _settingsFile = Path.Combine(dir, "settings.json");
    }

    public Task<string?> GetLastVaultAsync() => ReadAsync(settings => settings.LastVaultPath);

    public Task SaveLastVaultAsync(string? path) => UpdateAsync(settings => settings.LastVaultPath = path);

    public async Task<NavigationState> GetNavigationStateAsync(string? vaultPath)
    {
        return await ReadAsync(settings =>
        {
            if (!string.Equals(settings.NavigationVaultPath, vaultPath, StringComparison.OrdinalIgnoreCase))
                return new NavigationState(vaultPath, null, null, null);
            return new NavigationState(vaultPath, settings.LastNotebookId, settings.LastSectionId, settings.LastPageId);
        });
    }

    public Task SaveNavigationStateAsync(string? vaultPath, string? notebookId, string? sectionId, string? pageId) =>
        UpdateAsync(settings =>
        {
            settings.NavigationVaultPath = vaultPath;
            settings.LastNotebookId = notebookId;
            settings.LastSectionId = sectionId;
            settings.LastPageId = pageId;
        });

    public async Task<UiLayoutSettings> GetUiLayoutAsync()
    {
        return await ReadAsync(settings =>
        {
            var ribbonMode = Enum.TryParse<RibbonDisplayMode>(settings.RibbonMode, true, out var parsed)
                ? parsed
                : RibbonDisplayMode.Expanded;
            return new UiLayoutSettings(
                ribbonMode,
                settings.PageHeaderHeight ?? 66,
                settings.NavigationWidth ?? 220,
                settings.PagesWidth ?? 276,
                settings.NavigationVisible ?? true,
                settings.PagesVisible ?? true);
        });
    }

    public Task SaveUiLayoutAsync(UiLayoutSettings layout) =>
        UpdateAsync(settings =>
        {
            settings.RibbonMode = layout.RibbonMode.ToString();
            settings.PageHeaderHeight = layout.PageHeaderHeight;
            settings.NavigationWidth = layout.NavigationWidth;
            settings.PagesWidth = layout.PagesWidth;
            settings.NavigationVisible = layout.NavigationVisible;
            settings.PagesVisible = layout.PagesVisible;
        });

    private async Task<T> ReadAsync<T>(Func<Settings, T> selector)
    {
        await SettingsGate.WaitAsync();
        try
        {
            return selector(await LoadUnlockedAsync());
        }
        finally
        {
            SettingsGate.Release();
        }
    }

    private async Task UpdateAsync(Action<Settings> update)
    {
        await SettingsGate.WaitAsync();
        try
        {
            var settings = await LoadUnlockedAsync();
            update(settings);
            await SaveUnlockedAsync(settings);
        }
        finally
        {
            SettingsGate.Release();
        }
    }

    private async Task<Settings> LoadUnlockedAsync()
    {
        if (!File.Exists(_settingsFile)) return new Settings();
        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            return JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        }
        catch
        {
            // A damaged settings file must never prevent the application from
            // opening.  The next successful save will replace it atomically.
            return new Settings();
        }
    }

    private async Task SaveUnlockedAsync(Settings settings)
    {
        var temp = $"{_settingsFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, Options);
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, _settingsFile, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private sealed class Settings
    {
        public string? LastVaultPath { get; set; }
        public string? RibbonMode { get; set; }
        public double? PageHeaderHeight { get; set; }
        public double? NavigationWidth { get; set; }
        public double? PagesWidth { get; set; }
        public bool? NavigationVisible { get; set; }
        public bool? PagesVisible { get; set; }
        public string? NavigationVaultPath { get; set; }
        public string? LastNotebookId { get; set; }
        public string? LastSectionId { get; set; }
        public string? LastPageId { get; set; }
    }
}

public sealed record UiLayoutSettings(
    RibbonDisplayMode RibbonMode,
    double PageHeaderHeight,
    double NavigationWidth,
    double PagesWidth,
    bool NavigationVisible,
    bool PagesVisible);
