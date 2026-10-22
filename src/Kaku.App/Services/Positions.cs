using System.IO;
using System.Text.Json;
using Kaku.Core.Navigation;

namespace Kaku.App.Services;

/// <summary>Last reading position of every book, in %APPDATA%\Kaku\positions.json (keyed by the book's full path).</summary>
public sealed class PositionStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "positions.json");
    private Dictionary<string, ReadingPosition> _positions = new(StringComparer.OrdinalIgnoreCase);

    public static PositionStore Load()
    {
        var store = new PositionStore();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, ReadingPosition>>(File.ReadAllText(FilePath)) is { } d)
                store._positions = new Dictionary<string, ReadingPosition>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // unreadable: start empty
        }
        return store;
    }

    public ReadingPosition? Get(string bookPath) => _positions.GetValueOrDefault(Path.GetFullPath(bookPath));

    public void Set(string bookPath, ReadingPosition p) => _positions[Path.GetFullPath(bookPath)] = p;

    public void Move(string from, string to)
    {
        if (_positions.Remove(Path.GetFullPath(from), out var p)) _positions[Path.GetFullPath(to)] = p;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            // only the 200 most recent books are kept
            var keep = _positions.OrderByDescending(kv => kv.Value.Saved).Take(200).ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(keep, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // a convenience: never fail because of it
        }
    }
}
