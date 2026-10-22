using System.IO;
using System.Text.Json;

namespace Kaku.App.Services;

/// <summary>A search kept in the "Búsquedas" menu.</summary>
public sealed record SavedSearch(string Name, string Pattern, bool Regex, bool MatchCase = false, bool Code = false);

/// <summary>Per-user settings in %APPDATA%\Kaku\settings.json.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 12;

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaku");

    /// <summary>Machine-local data: backups, crash recovery, WebView2 cache.</summary>
    public static string LocalFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kaku");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public List<string> RecentFiles { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }
    public double LeftPanelWidth { get; set; } = 300;
    public double RightPanelWidth { get; set; } = 460;
    public bool ShowLeftPanel { get; set; } = true;
    public bool ShowPreview { get; set; } = true;
    public bool ShowBottomPanel { get; set; }
    public double BottomPanelHeight { get; set; } = 240;
    public List<SavedSearch> SavedSearches { get; set; } = new();
    public bool SpellCheck { get; set; } = true;
    public string? EpubcheckJar { get; set; }
    public List<string> RecentSymbols { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // corrupt settings: start fresh
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // settings are a convenience; never fail because of them
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
    }
}
