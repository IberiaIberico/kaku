using System.IO;
using System.Text.Json;
using Kaku.Core.Reference;

namespace Kaku.App.Services;

/// <summary>The original linked to a book, and the paragraph pairs pinned by hand.</summary>
public sealed class ReferenceLink
{
    public string Reference { get; set; } = "";

    /// <summary>Translated chapter path → pinned (translated block, original block) pairs.</summary>
    public Dictionary<string, List<int[]>> Pins { get; set; } = new();

    public Dictionary<string, List<Aligner.Pin>> ToPins() =>
        Pins.ToDictionary(kv => kv.Key, kv => kv.Value.Where(p => p.Length == 2).Select(p => new Aligner.Pin(p[0], p[1])).ToList());
}

/// <summary>
/// Links between translated books and their originals, in %APPDATA%\Kaku\references.json (outside the
/// EPUB, so the translated book stays clean). Keyed by the book's full path.
/// </summary>
public sealed class ReferenceStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "references.json");
    private Dictionary<string, ReferenceLink> _links = new(StringComparer.OrdinalIgnoreCase);

    public static ReferenceStore Load()
    {
        var store = new ReferenceStore();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, ReferenceLink>>(File.ReadAllText(FilePath)) is { } d)
                store._links = new Dictionary<string, ReferenceLink>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // unreadable: start empty
        }
        return store;
    }

    public ReferenceLink? Get(string bookPath) => _links.GetValueOrDefault(Path.GetFullPath(bookPath));

    public ReferenceLink Set(string bookPath, string referencePath)
    {
        var key = Path.GetFullPath(bookPath);
        if (!_links.TryGetValue(key, out var link) || !string.Equals(link.Reference, referencePath, StringComparison.OrdinalIgnoreCase))
            _links[key] = link = new ReferenceLink { Reference = Path.GetFullPath(referencePath) };
        return link;
    }

    public void Remove(string bookPath) => _links.Remove(Path.GetFullPath(bookPath));

    public void Move(string from, string to)
    {
        if (_links.Remove(Path.GetFullPath(from), out var link)) _links[Path.GetFullPath(to)] = link;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(_links, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // a convenience: never fail because of it
        }
    }
}
