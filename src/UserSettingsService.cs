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

    public UserSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalNote");
        Directory.CreateDirectory(dir);
        _settingsFile = Path.Combine(dir, "settings.json");
    }

    public async Task<string?> GetLastVaultAsync() => (await LoadAsync()).LastVaultPath;

    public async Task SaveLastVaultAsync(string? path)
    {
        var settings = await LoadAsync();
        settings.LastVaultPath = path;
        await SaveAsync(settings);
    }

    public async Task<NavigationState> GetNavigationStateAsync(string? vaultPath)
    {
        var settings = await LoadAsync();
        if (!string.Equals(settings.NavigationVaultPath, vaultPath, StringComparison.OrdinalIgnoreCase))
            return new NavigationState(vaultPath, null, null, null);
        return new NavigationState(vaultPath, settings.LastNotebookId, settings.LastSectionId, settings.LastPageId);
    }

    public async Task SaveNavigationStateAsync(string? vaultPath, string? notebookId, string? sectionId, string? pageId)
    {
        var settings = await LoadAsync();
        settings.NavigationVaultPath = vaultPath;
        settings.LastNotebookId = notebookId;
        settings.LastSectionId = sectionId;
        settings.LastPageId = pageId;
        await SaveAsync(settings);
    }

    public async Task<UiLayoutSettings> GetUiLayoutAsync()
    {
        var settings = await LoadAsync();
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
    }

    public async Task SaveUiLayoutAsync(UiLayoutSettings layout)
    {
        var settings = await LoadAsync();
        settings.RibbonMode = layout.RibbonMode.ToString();
        settings.PageHeaderHeight = layout.PageHeaderHeight;
        settings.NavigationWidth = layout.NavigationWidth;
        settings.PagesWidth = layout.PagesWidth;
        settings.NavigationVisible = layout.NavigationVisible;
        settings.PagesVisible = layout.PagesVisible;
        await SaveAsync(settings);
    }

    private async Task<Settings> LoadAsync()
    {
        if (!File.Exists(_settingsFile)) return new Settings();
        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            return JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    private async Task SaveAsync(Settings settings)
    {
        var temp = _settingsFile + ".tmp";
        var json = JsonSerializer.Serialize(settings, Options);
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, _settingsFile, true);
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
