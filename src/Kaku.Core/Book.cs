using System.Text;
using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core;

public sealed record HeadingRef(string Title, int BlockIndex, int Level);

/// <summary>A spine document with its text model and position data. Reloaded after every edit.</summary>
public sealed class Chapter
{
    private Dictionary<XElement, ElementRange> _ranges = new();

    public int Index { get; internal set; }
    public required ManifestItem Item { get; set; }
    public string Path => Item.Path;
    public bool Linear { get; internal set; }

    /// <summary>Current XHTML source (edited text, if modified).</summary>
    public string SourceText { get; private set; } = "";
    /// <summary>Source as it was loaded or last saved; null for a page added since.</summary>
    public string? SavedText { get; internal set; } = "";
    public bool IsModified => SavedText == null || !ReferenceEquals(SourceText, SavedText) && SourceText != SavedText;

    /// <summary>Increments on every reload: search results and other cached positions check it.</summary>
    public int Revision { get; private set; }

    public XDocument? Document { get; private set; }
    public string? ParseError { get; private set; }
    /// <summary>1-based line of the parse error, 0 if none/unknown.</summary>
    public int ParseErrorLine { get; private set; }
    public List<ContentBlock> Blocks { get; private set; } = new();
    public Dictionary<string, int> IdToBlock { get; private set; } = new();

    public string Title { get; internal set; } = "";
    /// <summary>Chapter this one belongs under in the chapter tree (TOC nesting or a file continuing a chapter).</summary>
    public Chapter? Parent { get; internal set; }
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
    /// <summary>True for a file that continues the previous titled chapter (not in the TOC, no heading).</summary>
    public bool IsContinuation { get; internal set; }
    public List<HeadingRef> Headings { get; } = new();
    public int WordCount { get; private set; }
    /// <summary>Number of words in the book before this chapter.</summary>
    public long WordStart { get; internal set; }
    public int NoteRefCount { get; private set; }
    public bool IsImageOnly { get; private set; }
    public string? FirstImagePath { get; private set; }
    public string Snippet { get; private set; } = "";
    /// <summary>Folded full text, for accent-insensitive filtering.</summary>
    public string SearchText { get; private set; } = "";

    /// <summary>Last block whose source starts at or before the given 1-based line/column.</summary>
    public int BlockAtSource(int line, int column)
    {
        int best = 0;
        for (int i = 0; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            if (b.SourceLine < line || b.SourceLine == line && b.SourceColumn <= column) best = i;
            else break;
        }
        return best;
    }

    /// <summary>Last block whose element starts at or before the given source offset.</summary>
    public int BlockAtOffset(int offset)
    {
        int best = 0;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].SourceOffset <= offset) best = i;
            else break;
        }
        return best;
    }

    /// <summary>Source range of an element of the current document.</summary>
    public ElementRange? RangeOf(XElement e) => _ranges.TryGetValue(e, out var r) ? r : null;

    internal static Chapter Load(int index, SpineItem spine, ManifestItem item, byte[] data, string? savedText)
    {
        var c = new Chapter { Index = index, Item = item, Linear = spine.Linear };
        var text = XmlLoader.DecodeText(data);
        c.SavedText = savedText == text ? text : savedText;
        c.Reload(text);
        return c;
    }

    /// <summary>Replaces the source and rebuilds the text model. If the XML is broken, the source is kept
    /// (code mode keeps typing) but the previous text model stays and <see cref="ParseError"/> is set.</summary>
    public void Reload(string source)
    {
        Revision++;
        SourceText = source;
        XDocument doc;
        ExtractionResult extraction;
        try
        {
            doc = XmlLoader.Parse(source);
            extraction = TextExtractor.Extract(doc, Path);
        }
        catch (System.Xml.XmlException ex)
        {
            ParseError = ex.Message;
            ParseErrorLine = ex.LineNumber;
            return;
        }
        ParseError = null;
        ParseErrorLine = 0;
        Document = doc;

        _ranges = new Dictionary<XElement, ElementRange>();
        var ranges = XmlSourceScanner.Scan(source);
        int k = 0;
        foreach (var e in doc.Root!.DescendantsAndSelf())
        {
            if (k >= ranges.Count) break;
            _ranges[e] = ranges[k++];
        }

        var blocks = extraction.Blocks;
        foreach (var b in blocks)
        {
            if (b.Element != null && _ranges.TryGetValue(b.Element, out var r))
                b.SourceOffset = b.IsAnonymous ? r.ContentStart : r.OuterStart;
        }
        var imagePaths = blocks.SelectMany(b => b.Spans).Where(s => s.Kind == SpanKind.Image && s.Data != null).Select(s => s.Data!).ToList();
        var snippetBlock = blocks.FirstOrDefault(b => b.Kind == BlockKind.Paragraph && !b.IsNote && b.WordCount > 0);
        var sb = new StringBuilder();
        foreach (var b in blocks) sb.Append(b.Text).Append('\n');

        Blocks = blocks;
        IdToBlock = extraction.IdToBlock;
        WordCount = blocks.Where(b => !b.IsNote).Sum(b => b.WordCount);
        NoteRefCount = blocks.Sum(b => b.Spans.Count(s => s.Kind == SpanKind.NoteRef));
        IsImageOnly = blocks.Count > 0 && blocks.All(b => b.Kind == BlockKind.Image || b.WordCount == 0) && imagePaths.Count > 0;
        FirstImagePath = imagePaths.FirstOrDefault();
        Snippet = snippetBlock == null ? "" : Shorten(snippetBlock.WordText, 90);
        SearchText = TextNormalizer.Fold(sb.ToString());
    }

    internal static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}

/// <summary>A location in the text: chapter, block and character offset within the block's visible text.</summary>
public readonly record struct TextLocation(int Chapter, int Block, int Offset);

/// <summary>An opened EPUB: raw package, package document, TOC and the text model of every spine document.</summary>
public sealed class Book
{
    private static readonly System.Text.UTF8Encoding Utf8 = new(false);

    /// <summary>Content of every file at the last load/save: what "modified" is measured against.</summary>
    private Dictionary<string, byte[]> _saved = new(StringComparer.Ordinal);
    private List<string> _savedOrder = new();

    public required string FilePath { get; set; }
    public required EpubPackage Package { get; init; }
    public required OpfDocument Opf { get; set; }
    public List<TocEntry> Toc { get; set; } = new();
    public List<Chapter> Chapters { get; } = new();
    public long TotalWords { get; private set; }

    public string Title => Opf.Title.Length > 0 ? Opf.Title : System.IO.Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>Non-chapter files whose content differs from the saved book (CSS, OPF, images, …).</summary>
    public IReadOnlyCollection<string> ModifiedFiles =>
        Package.Entries.Where(e => ChapterByPath(e.Path) == null && IsEntryModified(e)).Select(e => e.Path).ToList();

    /// <summary>True if anything differs from the saved book: text, files, or the set/order of files.</summary>
    public bool IsModified =>
        Chapters.Any(c => c.IsModified)
        || Package.Entries.Count != _savedOrder.Count
        || !Package.Entries.Select(e => e.Path).SequenceEqual(_savedOrder)
        || Package.Entries.Any(e => ChapterByPath(e.Path) == null && IsEntryModified(e));

    public bool StructureModified =>
        Package.Entries.Count != _savedOrder.Count || !Package.Entries.Select(e => e.Path).SequenceEqual(_savedOrder)
        || Package.Find(Opf.Path) is { } opf && IsEntryModified(opf);

    private bool IsEntryModified(EpubEntry e) =>
        !_saved.TryGetValue(e.Path, out var data) || !ReferenceEquals(data, e.Data) && !data.AsSpan().SequenceEqual(e.Data);

    /// <summary>Saved text of a file (null if it did not exist at the last save).</summary>
    public string? SavedTextOf(string path) => _saved.TryGetValue(path, out var d) ? XmlLoader.DecodeText(d) : null;

    private void MarkAllSaved()
    {
        _saved = Package.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.Ordinal);
        _savedOrder = Package.Entries.Select(e => e.Path).ToList();
        foreach (var c in Chapters) c.SavedText = c.SourceText;
    }

    /// <summary>Replaces the content of a non-chapter text file (code mode on CSS, …).</summary>
    public void SetFileText(string path, string text)
    {
        var entry = Package.Find(path) ?? throw new FileNotFoundException(path);
        entry.Data = Utf8.GetBytes(text);
    }

    /// <summary>Writes every chapter's current source into its package entry (before structural changes and saving).</summary>
    public void FlushChapters()
    {
        foreach (var c in Chapters)
        {
            var entry = Package.Find(c.Path);
            if (entry != null && XmlLoader.DecodeText(entry.Data) != c.SourceText) entry.Data = Utf8.GetBytes(c.SourceText);
        }
    }

    /// <summary>
    /// Writes the book to <paramref name="path"/>: atomic and verified.
    /// The book then points to that path and nothing is left modified.
    /// </summary>
    public void Save(string path)
    {
        FlushChapters();
        Package.Save(path);
        MarkAllSaved();
        FilePath = path;
    }

    public static Book Load(string path) => Open(EpubPackage.Load(path), path, baseline: null);

    /// <summary>
    /// Opens unsaved work kept by crash recovery (<paramref name="contentPath"/>) as the book at
    /// <paramref name="originalPath"/>: the differences from the original show as modified.
    /// </summary>
    public static Book LoadRecovered(string originalPath, string contentPath) =>
        Open(EpubPackage.Load(contentPath), originalPath, File.Exists(originalPath) ? EpubPackage.Load(originalPath) : null);

    private static Book Open(EpubPackage pkg, string path, EpubPackage? baseline)
    {
        var opf = OpfDocument.Load(pkg);
        var book = new Book { FilePath = path, Package = pkg, Opf = opf, Toc = TocReader.Read(pkg, opf) };
        var basePkg = baseline ?? pkg;
        book._saved = basePkg.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.Ordinal);
        book._savedOrder = basePkg.Entries.Select(e => e.Path).ToList();
        foreach (var s in opf.Spine)
        {
            if (s.Item is not { IsXhtml: true } item) continue;
            var data = pkg.Read(item.Path);
            if (data == null) continue;
            book.Chapters.Add(Chapter.Load(book.Chapters.Count, s, item, data, book.SavedTextOf(item.Path)));
        }
        book.RefreshPositions();
        book.RefreshTitles();
        return book;
    }

    /// <summary>
    /// Re-reads OPF, TOC and spine from the package after a structural change. Chapter objects are kept
    /// for files that still exist (following <paramref name="renamed"/>: old path → new path), so open
    /// editors and undo histories stay attached; a kept chapter reloads if its file text changed.
    /// Returns the chapters whose text was reloaded.
    /// </summary>
    public List<Chapter> RebuildModel(IReadOnlyDictionary<string, string>? renamed = null)
    {
        Opf = OpfDocument.Load(Package);
        Toc = TocReader.Read(Package, Opf);
        var byPath = new Dictionary<string, Chapter>(StringComparer.Ordinal);
        foreach (var c in Chapters)
            byPath[renamed != null && renamed.TryGetValue(c.Path, out var np) ? np : c.Path] = c;

        var reloaded = new List<Chapter>();
        Chapters.Clear();
        foreach (var s in Opf.Spine)
        {
            if (s.Item is not { IsXhtml: true } item || Package.Read(item.Path) is not { } data) continue;
            var text = XmlLoader.DecodeText(data);
            if (byPath.Remove(item.Path, out var c))
            {
                c.Item = item;
                c.Index = Chapters.Count;
                c.Linear = s.Linear;
                if (c.SourceText != text)
                {
                    c.Reload(text);
                    reloaded.Add(c);
                }
                // a renamed file is new to the saved book
                if (!_saved.ContainsKey(item.Path)) c.SavedText = null;
                else if (c.SavedText == null) c.SavedText = SavedTextOf(item.Path);
                Chapters.Add(c);
            }
            else
            {
                Chapters.Add(Chapter.Load(Chapters.Count, s, item, data, SavedTextOf(item.Path)));
            }
        }
        RefreshPositions();
        RefreshTitles();
        return reloaded;
    }

    /// <summary>Recomputes word positions (after edits).</summary>
    public void RefreshPositions()
    {
        long words = 0;
        foreach (var c in Chapters)
        {
            c.WordStart = words;
            words += c.WordCount;
        }
        TotalWords = words;
    }

    /// <summary>Recomputes titles, the chapter tree and headings (after edits).</summary>
    public void RefreshTitles()
    {
        foreach (var ch in Chapters)
        {
            ch.Parent = null;
            ch.IsContinuation = false;
            ch.Headings.Clear();
        }
        Chapter? continued = null; // last titled text chapter, for files that continue it
        int part = 1;
        var titled = new List<(Chapter Chapter, int Level)>(); // open TOC levels, for nesting
        foreach (var c in Chapters)
        {
            var entries = Toc.Where(t => t.Path == c.Path).ToList();
            var main = entries.FirstOrDefault(t => t.Fragment == null || c.IdToBlock.GetValueOrDefault(t.Fragment, 0) == 0) ?? entries.FirstOrDefault();
            var firstHeading = c.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Heading && !b.IsNote);
            var tocTitle = main?.Title is { Length: > 0 } tt ? tt : null;
            if (tocTitle != null || firstHeading != null)
            {
                c.Title = tocTitle ?? Chapter.Shorten(firstHeading!.Text, 60);
                int level = main?.Level ?? 0;
                while (titled.Count > 0 && titled[^1].Level >= level) titled.RemoveAt(titled.Count - 1);
                c.Parent = titled.Count > 0 ? titled[^1].Chapter : null;
                titled.Add((c, level));
                continued = c;
                part = 1;
            }
            else if (continued != null && c.WordCount > 0)
            {
                // Japanese books often split one chapter into several files; only the first is in the TOC
                c.Title = $"{continued.Title} ({++part})";
                c.Parent = continued;
                c.IsContinuation = true;
            }
            else
            {
                c.Title = EpubPath.FileName(c.Path);
                if (c.WordCount > 0)
                {
                    continued = null;
                    titled.Clear();
                }
            }

            var heads = new Dictionary<int, HeadingRef>();
            foreach (var b in c.Blocks.Where(b => b.Kind == BlockKind.Heading && !b.IsNote))
                heads[b.Index] = new HeadingRef(Chapter.Shorten(b.Text, 60), b.Index, b.HeadingLevel);
            foreach (var e in entries.Where(e => e.Fragment != null))
                if (c.IdToBlock.TryGetValue(e.Fragment!, out var bi) && !heads.ContainsKey(bi))
                    heads[bi] = new HeadingRef(e.Title, bi, e.Level + 1);
            c.Headings.AddRange(heads.Values.OrderBy(h => h.BlockIndex));
        }
    }

    // ------------------------------------------------------------------ positions

    /// <summary>Number of words in the book before the location (0-based word index).</summary>
    public long WordIndexAt(TextLocation loc)
    {
        if (Chapters.Count == 0) return 0;
        var c = Chapters[Math.Clamp(loc.Chapter, 0, Chapters.Count - 1)];
        if (c.Blocks.Count == 0) return c.WordStart;
        var b = c.Blocks[Math.Clamp(loc.Block, 0, c.Blocks.Count - 1)];
        int inBlock = b.IsNote ? 0 : WordCounter.CountBefore(b.WordText, loc.Offset);
        return c.WordStart + b.WordStart + inBlock;
    }

    public double PercentAt(long wordIndex) => TotalWords == 0 ? 0 : Math.Clamp(wordIndex * 100.0 / TotalWords, 0, 100);

    public double PercentAt(TextLocation loc) => PercentAt(WordIndexAt(loc));

    /// <summary>Location of the start of the given word (0-based).</summary>
    public TextLocation LocationOfWord(long wordIndex)
    {
        if (Chapters.Count == 0) return default;
        wordIndex = Math.Clamp(wordIndex, 0, Math.Max(0, TotalWords - 1));
        int ci = 0;
        for (int i = 0; i < Chapters.Count; i++)
        {
            if (Chapters[i].WordCount > 0 && Chapters[i].WordStart <= wordIndex) ci = i;
        }
        var c = Chapters[ci];
        long local = wordIndex - c.WordStart;
        int bi = 0;
        for (int i = 0; i < c.Blocks.Count; i++)
        {
            var b = c.Blocks[i];
            if (!b.IsNote && b.WordCount > 0 && b.WordStart <= local) bi = i;
        }
        if (c.Blocks.Count == 0) return new TextLocation(ci, 0, 0);
        var block = c.Blocks[bi];
        return new TextLocation(ci, bi, WordCounter.OffsetOfWord(block.WordText, (int)(local - block.WordStart)));
    }

    public TextLocation LocationOfPercent(double percent) =>
        LocationOfWord((long)Math.Floor(Math.Clamp(percent, 0, 100) / 100.0 * TotalWords));

    public Chapter? ChapterByPath(string path) =>
        Chapters.FirstOrDefault(c => c.Path == path) ?? Chapters.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
}
