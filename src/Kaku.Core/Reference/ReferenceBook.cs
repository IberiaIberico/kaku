using System.IO.Compression;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Reference;

/// <summary>A candidate original found on disk, with how well its structure matches the book.</summary>
public sealed record ReferenceCandidate(string Path, string Title, string Language, double Score);

/// <summary>
/// The original (Japanese) EPUB opened read-only next to the translation: which original chapter matches
/// each translated chapter, and the paragraph alignment inside it (cached per chapter revision).
/// </summary>
public sealed class ReferenceBook
{
    private readonly Dictionary<Chapter, (int Revision, Aligner.Alignment Alignment)> _cache = new();

    private ReferenceBook(Book original) => Original = original;

    public Book Original { get; }
    public string Path => Original.FilePath;

    public static ReferenceBook Load(string path) => new(Book.Load(path));

    /// <summary>Pinned pairs per translated chapter path (set by the app from its stored links).</summary>
    public Dictionary<string, List<Aligner.Pin>> Pins { get; } = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ chapters

    /// <summary>
    /// The original chapter for a translated one: the same file path, otherwise the unmatched original
    /// chapter at the same relative place between two matched neighbours. Null for pages added in translation.
    /// </summary>
    public Chapter? ChapterFor(Book translated, Chapter chapter)
    {
        if (Original.ChapterByPath(chapter.Path) is { } same) return same;
        // between the nearest translated chapters that have a same-path counterpart
        int prev = -1, next = Original.Chapters.Count;
        int tPrev = -1, tNext = translated.Chapters.Count;
        for (int i = chapter.Index - 1; i >= 0; i--)
            if (Original.ChapterByPath(translated.Chapters[i].Path) is { } o) { prev = o.Index; tPrev = i; break; }
        for (int i = chapter.Index + 1; i < translated.Chapters.Count; i++)
            if (Original.ChapterByPath(translated.Chapters[i].Path) is { } o) { next = o.Index; tNext = i; break; }
        var free = Original.Chapters.Skip(prev + 1).Take(next - prev - 1)
            .Where(o => translated.ChapterByPath(o.Path) == null).ToList();
        var mine = translated.Chapters.Skip(tPrev + 1).Take(tNext - tPrev - 1)
            .Where(c => Original.ChapterByPath(c.Path) == null).ToList();
        // only when the gap has the same number of files on both sides (a rename, not an added page)
        if (free.Count == 0 || free.Count != mine.Count) return null;
        return free[mine.IndexOf(chapter)];
    }

    /// <summary>The translated chapter for an original one (for clicks in the reference view).</summary>
    public Chapter? TranslationFor(Book translated, Chapter original) =>
        translated.Chapters.FirstOrDefault(c => ChapterFor(translated, c) == original);

    // ------------------------------------------------------------------ paragraphs

    public Aligner.Alignment? AlignmentFor(Book translated, Chapter chapter)
    {
        if (ChapterFor(translated, chapter) is not { } original) return null;
        if (_cache.TryGetValue(chapter, out var c) && c.Revision == chapter.Revision) return c.Alignment;
        var alignment = Aligner.Align(chapter.Blocks, original.Blocks, Pins.GetValueOrDefault(chapter.Path));
        _cache[chapter] = (chapter.Revision, alignment);
        return alignment;
    }

    /// <summary>Original block for a translated block (-1 if there is none).</summary>
    public int OriginalBlock(Book translated, Chapter chapter, int block) =>
        AlignmentFor(translated, chapter) is { } a && block >= 0 && block < a.ToOriginal.Length ? a.ToOriginal[block] : -1;

    public int TranslatedBlock(Book translated, Chapter chapter, int originalBlock) =>
        AlignmentFor(translated, chapter) is { } a && originalBlock >= 0 && originalBlock < a.ToTranslation.Length ? a.ToTranslation[originalBlock] : -1;

    public void Invalidate(Chapter? chapter = null)
    {
        if (chapter == null) _cache.Clear();
        else _cache.Remove(chapter);
    }

    // ------------------------------------------------------------------ finding the original

    /// <summary>
    /// Scores EPUBs in the given folders by how many of the book's chapter files they contain at the same
    /// path (only the zip directory and the OPF are read, so this is fast).
    /// </summary>
    public static List<ReferenceCandidate> FindCandidates(Book book, IEnumerable<string> folders, int max = 15)
    {
        var mine = book.Chapters.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        // images survive translation byte for byte: (path, size) is a strong fingerprint of the edition
        var images = book.Package.Entries.Where(e => EpubPath.Extension(e.Path) is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
            .Select(e => (e.Path, (long)e.Data.Length)).ToHashSet();
        var self = System.IO.Path.GetFullPath(book.FilePath);
        var myLang = book.Opf.Language;
        var result = new List<ReferenceCandidate>();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.epub"))
            {
                if (string.Equals(System.IO.Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
                    int common = mine.Count(names.Contains);
                    if (common == 0) continue;
                    var (title, lang) = ReadMeta(zip);
                    int sameImages = zip.Entries.Count(e => images.Contains((e.FullName, e.Length)));
                    double score = (double)common / Math.Max(mine.Count, 1)
                                   + 2.0 * sameImages / Math.Max(images.Count, 1);
                    // another copy in the book's own language is a translation, not the original
                    if (lang.Length >= 2 && myLang.Length >= 2 && string.Equals(lang[..2], myLang[..2], StringComparison.OrdinalIgnoreCase)) score -= 3;
                    else if (lang.Length > 0) score += 0.5;
                    result.Add(new ReferenceCandidate(file, title, lang, score));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                }
            }
        }
        return result.Where(c => c.Score > 0).OrderByDescending(c => c.Score).Take(max).ToList();
    }

    private static (string Title, string Language) ReadMeta(ZipArchive zip)
    {
        try
        {
            var container = zip.GetEntry(EpubPackage.ContainerPath);
            if (container == null) return ("", "");
            using var cs = container.Open();
            var rootfile = XDocument.Load(cs).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
            var opfPath = (string?)rootfile?.Attribute("full-path");
            var opf = opfPath == null ? null : zip.GetEntry(opfPath);
            if (opf == null) return ("", "");
            using var os = opf.Open();
            var doc = XDocument.Load(os);
            string Dc(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n && e.Name.NamespaceName == "http://purl.org/dc/elements/1.1/")?.Value.Trim() ?? "";
            return (Dc("title"), Dc("language"));
        }
        catch (System.Xml.XmlException)
        {
            return ("", "");
        }
    }
}
