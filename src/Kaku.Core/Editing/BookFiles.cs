using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kaku.Core.Editing;

/// <summary>Copies of a book taken before each save: &lt;root&gt;/&lt;book name&gt;/&lt;yyyyMMdd-HHmmss&gt;.epub, newest kept.</summary>
public static class Backups
{
    public static string? Create(string bookPath, string root, int keep = 10)
    {
        if (!File.Exists(bookPath)) return null;
        var dir = Path.Combine(root, SafeName(Path.GetFileNameWithoutExtension(bookPath)));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.epub");
        File.Copy(bookPath, target, overwrite: true);
        foreach (var old in Directory.GetFiles(dir, "*.epub").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            File.Delete(old);
        return target;
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>What a recovery copy belongs to.</summary>
public sealed class RecoveryData
{
    public string BookPath { get; set; } = "";
    public long BookLength { get; set; }
    public DateTime BookWriteTimeUtc { get; set; }
    public DateTime SavedAt { get; set; }
    public string Title { get; set; } = "";
    public int ChangedFiles { get; set; }
}

/// <summary>
/// Unsaved work written periodically as a complete EPUB copy (&lt;hash&gt;.epub + &lt;hash&gt;.json), so text,
/// added/removed pages and images all survive a crash. Offered again only if the original is unchanged.
/// </summary>
public static class Recovery
{
    private static string Key(string bookPath) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(bookPath).ToLowerInvariant())))[..16];

    public static string MetaFile(string root, string bookPath) => Path.Combine(root, Key(bookPath) + ".json");

    public static string ContentFile(string root, string bookPath) => Path.Combine(root, Key(bookPath) + ".epub");

    public static void Write(string root, Book book)
    {
        Directory.CreateDirectory(root);
        book.FlushChapters();
        book.Package.Save(ContentFile(root, book.FilePath)); // atomic; does not mark the book as saved
        var info = new FileInfo(book.FilePath);
        var data = new RecoveryData
        {
            BookPath = book.FilePath,
            BookLength = info.Exists ? info.Length : 0,
            BookWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : default,
            SavedAt = DateTime.Now,
            Title = book.Title,
            ChangedFiles = book.Chapters.Count(c => c.IsModified) + book.ModifiedFiles.Count,
        };
        var meta = MetaFile(root, book.FilePath);
        File.WriteAllText(meta + ".tmp", JsonSerializer.Serialize(data));
        File.Move(meta + ".tmp", meta, overwrite: true);
    }

    public static RecoveryData? Read(string metaFile)
    {
        try
        {
            return JsonSerializer.Deserialize<RecoveryData>(File.ReadAllText(metaFile));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Recovery data for a book, if any and if the file on disk has not changed since.</summary>
    public static RecoveryData? Find(string root, string bookPath)
    {
        var meta = MetaFile(root, bookPath);
        if (!File.Exists(meta) || !File.Exists(ContentFile(root, bookPath)) || Read(meta) is not { } data) return null;
        var info = new FileInfo(bookPath);
        return info.Exists && info.Length == data.BookLength && info.LastWriteTimeUtc == data.BookWriteTimeUtc ? data : null;
    }

    public static IEnumerable<RecoveryData> All(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.json").Select(Read).OfType<RecoveryData>().OrderByDescending(d => d.SavedAt)
            : [];

    public static void Delete(string root, string bookPath)
    {
        foreach (var f in new[] { MetaFile(root, bookPath), ContentFile(root, bookPath) })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Opens the recovered copy as the original book (differences show as unsaved changes).</summary>
    public static Book Open(string root, RecoveryData data) => Book.LoadRecovered(data.BookPath, ContentFile(root, data.BookPath));
}
