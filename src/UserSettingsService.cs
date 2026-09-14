using System.IO;
using System.Text.Json;

namespace LocalNote.App.Services;

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

    public async Task<UiLayoutSettings> GetUiLayoutAsync()
    {
        var settings = await LoadAsync();
        return new UiLayoutSettings(
            settings.RibbonHeight ?? 112,
            settings.PageHeaderHeight ?? 66,
            settings.NavigationWidth ?? 220,
            settings.PagesWidth ?? 276,
            settings.NavigationVisible ?? true,
            settings.PagesVisible ?? true);
    }

    public async Task SaveUiLayoutAsync(UiLayoutSettings layout)
    {
        var settings = await LoadAsync();
        settings.RibbonHeight = layout.RibbonHeight;
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
        public double? RibbonHeight { get; set; }
        public double? PageHeaderHeight { get; set; }
        public double? NavigationWidth { get; set; }
        public double? PagesWidth { get; set; }
        public bool? NavigationVisible { get; set; }
        public bool? PagesVisible { get; set; }
    }
}

public sealed record UiLayoutSettings(
    double RibbonHeight,
    double PageHeaderHeight,
    double NavigationWidth,
    double PagesWidth,
    bool NavigationVisible,
    bool PagesVisible);
