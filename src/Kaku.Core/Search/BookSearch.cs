using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kaku.Core.Editing;
using Kaku.Core.Epub;

namespace Kaku.Core.Search;

public enum SearchScope
{
    /// <summary>Visible text of the chapters (what you read; tags hidden).</summary>
    Text,
    /// <summary>XHTML source of the chapters and the other text files (CSS, …).</summary>
    Code,
}

public sealed record SearchQuery(
    string Pattern,
    bool MatchCase = false,
    bool WholeWord = false,
    bool IgnoreAccents = false,
    bool UseRegex = false,
    SearchScope Scope = SearchScope.Text)
{
    /// <summary>The regex for this query (plain text is escaped). Null and an error message if the pattern is invalid.</summary>
    public Regex? Build(out string? error)
    {
        error = null;
        if (Pattern.Length == 0) return null;
        var p = UseRegex ? Pattern : Regex.Escape(Pattern);
        if (IgnoreAccents) p = BookSearch.FoldAccents(p); // only non-ASCII letters change: regex syntax is untouched
        if (WholeWord) p = $@"(?<![\p{{L}}\p{{N}}_])(?:{p})(?![\p{{L}}\p{{N}}_])";
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(p, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>
/// One match. Text scope: <see cref="Block"/> and offsets in the block's visible text.
/// Code scope: offsets in the file source (<see cref="Block"/> = -1, <see cref="Line"/> set).
/// </summary>
public sealed record SearchHit(
    string Path,
    int Chapter,
    int Block,
    int Start,
    int Length,
    int Line,
    int Revision,
    string Before,
    string Match,
    string After);

public sealed record SearchResult(List<SearchHit> Hits, bool Truncated, string? Error)
{
    public static readonly SearchResult Empty = new([], false, null);
}

public sealed record ReplaceOutcome(int Replaced, int Skipped, List<(Chapter Chapter, string Before)> ChapterUndo, List<(string Path, string Before)> FileUndo);

/// <summary>Find and replace across the book, in visible text (markup-safe) or in source.</summary>
public static class BookSearch
{
    private const int ContextBefore = 40;
    private const int ContextAfter = 70;
    private static readonly Dictionary<char, char> FoldCache = new();

    /// <summary>Removes diacritics without changing the length (é→e, ñ→n). Kana are left alone.</summary>
    public static string FoldAccents(string s)
    {
        char[]? chars = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x80 || c is >= '぀' and <= 'ヿ') continue;
            char f;
            lock (FoldCache)
            {
                if (!FoldCache.TryGetValue(c, out f))
                {
                    var d = c.ToString().Normalize(NormalizationForm.FormD);
                    f = d.Length > 1 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c;
                    FoldCache[c] = f;
                }
            }
            if (f != c) (chars ??= s.ToCharArray())[i] = f;
        }
        return chars == null ? s : new string(chars);
    }

    /// <summary>Text files searched in code scope besides the chapters (package files included, read-only).</summary>
    public static IEnumerable<EpubEntry> OtherTextFiles(Book book) =>
        book.Package.Entries.Where(e => book.ChapterByPath(e.Path) == null
            && EpubPath.Extension(e.Path) is ".css" or ".xhtml" or ".html" or ".htm" or ".xml" or ".opf" or ".ncx" or ".js" or ".txt" or ".svg");

    /// <summary>True for files that replace may not touch (structure is edited elsewhere).</summary>
    public static bool IsProtectedFile(Book book, string path) =>
        path == book.Opf.Path || path == book.Opf.NcxPath || path == book.Opf.NavPath || path == EpubPackage.ContainerPath || path == EpubPackage.MimetypePath;

    public static SearchResult Find(Book book, SearchQuery query, IReadOnlyCollection<int>? chapters = null, int maxHits = 10_000)
    {
        var regex = query.Build(out var error);
        if (regex == null) return new SearchResult([], false, error);
        var hits = new List<SearchHit>();
        bool truncated = false;
        try
        {
            foreach (var c in book.Chapters)
            {
                if (chapters != null && !chapters.Contains(c.Index)) continue;
                if (query.Scope == SearchScope.Text) FindInChapterText(c, query, regex, hits, maxHits);
                else FindInSource(c.Path, c.Index, c.Revision, c.SourceText, query, regex, hits, maxHits);
                if (hits.Count >= maxHits) { truncated = true; break; }
            }
            if (!truncated && query.Scope == SearchScope.Code && chapters == null)
            {
                foreach (var e in OtherTextFiles(book))
                {
                    FindInSource(e.Path, -1, 0, XmlLoader.DecodeText(e.Data), query, regex, hits, maxHits);
                    if (hits.Count >= maxHits) { truncated = true; break; }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchResult(hits, true, "La expresión regular tarda demasiado.");
        }
        return new SearchResult(hits, truncated, null);
    }

    private static void FindInChapterText(Chapter c, SearchQuery q, Regex regex, List<SearchHit> hits, int max)
    {
        foreach (var b in c.Blocks)
        {
            var text = q.IgnoreAccents ? FoldAccents(b.WordText) : b.WordText; // placeholders blanked
            foreach (Match m in regex.Matches(text))
            {
                if (m.Length == 0) continue;
                hits.Add(MakeHit(c.Path, c.Index, b.Index, m.Index, m.Length, 0, c.Revision, b.Text));
                if (hits.Count >= max) return;
            }
        }
    }

    private static void FindInSource(string path, int chapter, int revision, string source, SearchQuery q, Regex regex, List<SearchHit> hits, int max)
    {
        var text = q.IgnoreAccents ? FoldAccents(source) : source;
        int line = 1, lineCountedTo = 0;
        foreach (Match m in regex.Matches(text))
        {
            if (m.Length == 0) continue;
            for (int i = lineCountedTo; i < m.Index; i++) if (source[i] == '\n') line++;
            lineCountedTo = m.Index;
            int ls = source.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            int le = source.IndexOf('\n', m.Index + m.Length);
            if (le < 0) le = source.Length;
            var lineText = source[ls..le];
            var hit = MakeHit(path, chapter, -1, m.Index, m.Length, line, revision, lineText, m.Index - ls);
            hits.Add(hit);
            if (hits.Count >= max) return;
        }
    }

    private static SearchHit MakeHit(string path, int chapter, int block, int start, int length, int line, int revision, string text, int? localStart = null)
    {
        int s = localStart ?? start;
        int e = Math.Min(text.Length, s + length);
        int b0 = Math.Max(0, s - ContextBefore);
        int a1 = Math.Min(text.Length, e + ContextAfter);
        var before = (b0 > 0 ? "…" : "") + text[b0..s].TrimStart();
        var after = text[e..a1].TrimEnd() + (a1 < text.Length ? "…" : "");
        return new SearchHit(path, chapter, block, start, length, line, revision, Flat(before), Flat(text[s..e]), Flat(after));
    }

    private static string Flat(string s) => s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    // ------------------------------------------------------------------ replace

    /// <summary>
    /// Replaces the given hits (from <see cref="Find"/> with the same query). Text-scope replacements go
    /// through <see cref="ChapterEditor"/>, so markup stays intact; a code-scope replacement that would break
    /// a chapter's XHTML is skipped. Returns the previous sources for undo.
    /// </summary>
    public static ReplaceOutcome Replace(Book book, SearchQuery query, IReadOnlyList<SearchHit> hits, string replacement)
    {
        var regex = query.Build(out _) ?? throw new ArgumentException("Búsqueda no válida.");
        int replaced = 0, skipped = 0;
        var chapterUndo = new List<(Chapter, string)>();
        var fileUndo = new List<(string, string)>();

        foreach (var group in hits.GroupBy(h => h.Path))
        {
            var chapter = book.ChapterByPath(group.Key);
            var selected = group.ToList();
            if (chapter != null && selected.Any(h => h.Revision != chapter.Revision))
            {
                skipped += selected.Count; // the chapter changed after the search
                continue;
            }

            if (query.Scope == SearchScope.Text && chapter != null)
            {
                var before = chapter.SourceText;
                var editor = new ChapterEditor(book, chapter);
                var perBlock = new Dictionary<int, List<TextChange>>();
                var requested = new Dictionary<int, int>();
                foreach (var byBlock in selected.GroupBy(h => h.Block))
                {
                    var block = chapter.Blocks[byBlock.Key];
                    var original = block.WordText;
                    var text = query.IgnoreAccents ? FoldAccents(original) : original;
                    var starts = byBlock.Select(h => h.Start).ToHashSet();
                    perBlock[byBlock.Key] = regex.Matches(text).Where(m => m.Length > 0 && starts.Contains(m.Index))
                        .Select(m => new TextChange(m.Index, m.Index + m.Length, query.UseRegex ? Expand(replacement, m, original) : replacement))
                        .ToList();
                    requested[byBlock.Key] = byBlock.Count();
                }
                // one splice + one reload for the whole chapter; loose-text blocks are retried one by one
                var rejected = editor.ApplyTextChangesBatch(perBlock);
                foreach (var (bi, changes) in perBlock)
                {
                    if (!rejected.Contains(bi)) replaced += changes.Count;
                    skipped += requested[bi] - changes.Count;
                }
                foreach (var bi in rejected.OrderByDescending(b => b))
                {
                    if (perBlock[bi].Count > 0 && editor.ApplyTextChanges(bi, perBlock[bi])) replaced += perBlock[bi].Count;
                    else skipped += perBlock[bi].Count;
                }
                if (chapter.SourceText != before) chapterUndo.Add((chapter, before));
            }
            else if (query.Scope == SearchScope.Code)
            {
                if (chapter == null && IsProtectedFile(book, group.Key))
                {
                    skipped += selected.Count;
                    continue;
                }
                var source = chapter?.SourceText ?? XmlLoader.DecodeText(book.Package.Find(group.Key)!.Data);
                var text = query.IgnoreAccents ? FoldAccents(source) : source;
                var starts = selected.Select(h => h.Start).ToHashSet();
                var sb = new StringBuilder(source);
                int count = 0;
                foreach (var m in regex.Matches(text).Where(m => m.Length > 0 && starts.Contains(m.Index)).OrderByDescending(m => m.Index))
                {
                    sb.Remove(m.Index, m.Length).Insert(m.Index, query.UseRegex ? Expand(replacement, m, source) : replacement);
                    count++;
                }
                var result = sb.ToString();
                if (chapter != null)
                {
                    var editor = new ChapterEditor(book, chapter);
                    if (editor.SetSource(result) != null)
                    {
                        editor.SetSource(source); // would break the XHTML: leave this chapter as it was
                        skipped += selected.Count;
                        continue;
                    }
                    chapterUndo.Add((chapter, source));
                }
                else
                {
                    book.SetFileText(group.Key, result);
                    fileUndo.Add((group.Key, source));
                }
                replaced += count;
                skipped += selected.Count - count;
            }
        }
        book.RefreshPositions();
        book.RefreshTitles();
        return new ReplaceOutcome(replaced, skipped, chapterUndo, fileUndo);
    }

    /// <summary>
    /// Expands $0–$9, ${name} and $$ using the <em>original</em> text (matching may run on accent-folded text,
    /// which has the same length, so group positions are valid in both).
    /// </summary>
    public static string Expand(string replacement, Match m, string original)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c != '$' || i + 1 >= replacement.Length)
            {
                sb.Append(c);
                continue;
            }
            char n = replacement[i + 1];
            if (n == '$')
            {
                sb.Append('$');
                i++;
            }
            else if (char.IsDigit(n))
            {
                var g = m.Groups[n - '0'];
                if (g.Success) sb.Append(original, g.Index, g.Length);
                i++;
            }
            else if (n == '{' && replacement.IndexOf('}', i + 2) is var close and > 0)
            {
                var g = m.Groups[replacement[(i + 2)..close]];
                if (g.Success) sb.Append(original, g.Index, g.Length);
                i = close;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
