using System.IO;
using System.Text.Json;

namespace Kaku.App.Services;

/// <summary>A bookmarked paragraph. Stored by chapter file path and block index.</summary>
public sealed class BookmarkData
{
    public string ChapterPath { get; set; } = "";
    public int Block { get; set; }
    public string Snippet { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>Bookmarks of every book, in %APPDATA%\Kaku\bookmarks.json (keyed by the book's full path).</summary>
public sealed class BookmarkStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "bookmarks.json");
    private Dictionary<string, List<BookmarkData>> _books = new(StringComparer.OrdinalIgnoreCase);

    public static BookmarkStore Load()
    {
        var store = new BookmarkStore();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, List<BookmarkData>>>(File.ReadAllText(FilePath)) is { } data)
                store._books = new Dictionary<string, List<BookmarkData>>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // unreadable file: start empty rather than fail
        }
        return store;
    }

    public List<BookmarkData> For(string bookPath)
    {
        var key = Path.GetFullPath(bookPath);
        if (!_books.TryGetValue(key, out var list)) _books[key] = list = new List<BookmarkData>();
        return list;
    }

    /// <summary>After "Guardar como": the bookmarks follow the book to its new path.</summary>
    public void Move(string from, string to)
    {
        var src = Path.GetFullPath(from);
        if (_books.Remove(src, out var list)) _books[Path.GetFullPath(to)] = list;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            foreach (var key in _books.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _books.Remove(key);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_books, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // bookmarks are a convenience
        }
    }
}
