using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Notes;

public enum NotePlacement
{
    /// <summary>One "Notas" page at the end of the book, numbering 1…N through the book.</summary>
    NotesPage,
    /// <summary>Notes at the end of each chapter file, numbering restarts per file.</summary>
    EndOfChapter,
}

public enum NoteMarkerStyle { Numbers, Brackets, Asterisk }

/// <summary>A note reference in the text.</summary>
public sealed record NoteMarker(string ChapterPath, string? ElementId, int BlockIndex, string Context);

/// <summary>A note managed by Kaku (markup with kaku-* ids and classes).</summary>
public sealed class BookNote
{
    public required int Uid { get; init; }
    public string Id => NoteIndex.NoteId(Uid);
    public string ContentXhtml { get; init; } = "";
    public string Text => MarkdownLite.PlainText(ContentXhtml);
    /// <summary>File and block where the note text currently is (empty if the note element is missing).</summary>
    public string? NotePath { get; init; }
    public int NoteBlock { get; init; } = -1;
    public List<NoteMarker> Markers { get; } = new();
    /// <summary>Number shown to the reader (1-based; per chapter or per book).</summary>
    public int Number { get; set; }
}

/// <summary>A note in a format Kaku did not write (publisher or older tools); can be converted.</summary>
public sealed record ForeignNote(string ChapterPath, int BlockIndex, string MarkerText, string TargetPath, string? Fragment, string Text, string Context);

public sealed class NoteIndexResult
{
    public List<BookNote> Notes { get; } = new();
    public List<ForeignNote> Foreign { get; } = new();
    /// <summary>Kaku markers whose note does not exist.</summary>
    public List<NoteMarker> OrphanMarkers { get; } = new();
    public NotePlacement Placement { get; set; }
    public NoteMarkerStyle Style { get; set; }
    public string? NotesPagePath { get; set; }

    public BookNote? ById(string id) => Notes.FirstOrDefault(n => n.Id == id);
}

/// <summary>Finds the notes of a book: Kaku's own (kaku-noteref / kaku-nota-N) and foreign ones (EPUB 3 noterefs, superscript links).</summary>
public static partial class NoteIndex
{
    public const string MarkerClass = "kaku-noteref";
    public const string NoteClass = "kaku-nota";
    public const string BackrefClass = "kaku-backref";
    public const string SectionClass = "kaku-notas";
    public const string OpsNs = "http://www.idpf.org/2007/ops";
    public const string MetaPlacement = "kaku:notes-placement";
    public const string MetaStyle = "kaku:notes-style";
    public const string MetaPage = "kaku:notes-page";

    public static string NoteId(int uid) => $"kaku-nota-{uid}";

    public static string RefId(int uid, int k) => k == 1 ? $"kaku-ref-{uid}" : $"kaku-ref-{uid}-{k}";

    [GeneratedRegex(@"^kaku-nota-(\d+)$")]
    private static partial Regex NoteIdRe();

    [GeneratedRegex(@"^\s*[\[(]?\s*(\d+|\*+|†+)\s*[\])]?\s*[.:]?\s*")]
    private static partial Regex LeadingNumberRe();

    public static int? UidOf(string? id) => id != null && NoteIdRe().Match(id) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    public static bool HasClass(XElement e, string cls) =>
        ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls);

    private static bool HasType(XElement e, params string[] types)
    {
        var t = XmlLoader.AttrLocal(e, "type");
        var role = (string?)e.Attribute("role");
        return t != null && t.Split(' ').Any(types.Contains) || role != null && types.Any(x => role == "doc-" + x);
    }

    /// <summary>Scans every chapter. <paramref name="textOf"/> gives a chapter's current XHTML.</summary>
    public static NoteIndexResult Build(Book book, Func<Chapter, string>? textOf = null)
    {
        var r = new NoteIndexResult();
        var opf = new Structure.OpfEditor(book.Package, book.Opf.Path);
        r.Placement = opf.GetMeta(MetaPlacement) == "chapter" ? NotePlacement.EndOfChapter : NotePlacement.NotesPage;
        r.Style = opf.GetMeta(MetaStyle) switch { "brackets" => NoteMarkerStyle.Brackets, "asterisk" => NoteMarkerStyle.Asterisk, _ => NoteMarkerStyle.Numbers };
        r.NotesPagePath = opf.GetMeta(MetaPage) is { } p && book.ChapterByPath(p) != null ? p : null;

        var notes = new Dictionary<int, BookNote>();
        var markers = new List<(int Uid, NoteMarker Marker)>();
        var docs = new Dictionary<string, XDocument>();
        foreach (var c in book.Chapters)
        {
            XDocument doc;
            ExtractionResult ex;
            try
            {
                doc = textOf == null && c.Document != null ? c.Document : XmlLoader.Parse(textOf?.Invoke(c) ?? c.SourceText);
                ex = TextExtractor.Extract(doc, c.Path);
            }
            catch (XmlException)
            {
                continue;
            }
            docs[c.Path] = doc;
            var blockOf = BlockLookup(ex);
            foreach (var e in doc.Descendants())
            {
                if (UidOf((string?)e.Attribute("id")) is { } uid && !notes.ContainsKey(uid))
                {
                    notes[uid] = new BookNote { Uid = uid, ContentXhtml = NoteContent(e), NotePath = c.Path, NoteBlock = blockOf(e) };
                }
                else if (e.Name.LocalName == "a" && HasClass(e, MarkerClass))
                {
                    var frag = EpubPath.SplitHref((string?)e.Attribute("href") ?? "").Fragment;
                    if (UidOf(frag) is { } muid) markers.Add((muid, new NoteMarker(c.Path, (string?)e.Attribute("id"), blockOf(e), Context(ex, blockOf(e), e))));
                }
            }
        }
        foreach (var (uid, m) in markers)
        {
            if (notes.TryGetValue(uid, out var n)) n.Markers.Add(m);
            else r.OrphanMarkers.Add(m);
        }
        // reading order: by the first marker (notes without markers go last)
        var order = book.Chapters.Select((c, i) => (c.Path, i)).ToDictionary(x => x.Path, x => x.i);
        r.Notes.AddRange(notes.Values.OrderBy(n => n.Markers.Count == 0 ? int.MaxValue : order[n.Markers[0].ChapterPath])
            .ThenBy(n => n.Markers.Count == 0 ? 0 : n.Markers[0].BlockIndex).ThenBy(n => n.Uid));
        Number(r);
        FindForeign(book, docs, r);
        return r;
    }

    /// <summary>Assigns the reader-visible numbers.</summary>
    public static void Number(NoteIndexResult r)
    {
        int n = 0;
        string? chapter = null;
        foreach (var note in r.Notes)
        {
            var home = note.Markers.FirstOrDefault()?.ChapterPath;
            if (r.Placement == NotePlacement.EndOfChapter && home != chapter)
            {
                chapter = home;
                n = 0;
            }
            note.Number = ++n;
        }
    }

    public static string MarkerText(NoteMarkerStyle style, int number) => style switch
    {
        NoteMarkerStyle.Brackets => $"[{number}]",
        NoteMarkerStyle.Asterisk => new string('*', Math.Min(number, 3)),
        _ => number.ToString(),
    };

    private static Func<XElement, int> BlockLookup(ExtractionResult ex)
    {
        var map = new Dictionary<XElement, int>();
        foreach (var b in ex.Blocks)
            if (b.Element != null) map.TryAdd(b.Element, b.Index);
        return e =>
        {
            for (var x = e; x != null; x = x.Parent)
                if (map.TryGetValue(x, out var i)) return i;
            // a container (aside) holding blocks: its first block
            var first = e.Descendants().FirstOrDefault(map.ContainsKey);
            return first != null ? map[first] : 0;
        };
    }

    /// <summary>The words just before a marker ("…cogió el *senpai*").</summary>
    private static string Context(ExtractionResult ex, int block, XElement marker)
    {
        if (block < 0 || block >= ex.Blocks.Count) return "";
        var b = ex.Blocks[block];
        var span = b.Spans.FirstOrDefault(s => s.Kind == SpanKind.NoteRef && s.Data == (string?)marker.Attribute("href"));
        int end = span == default ? b.Text.Length : span.Start;
        var before = b.Text[..end].TrimEnd();
        return before.Length <= 40 ? before : "…" + before[^40..];
    }

    /// <summary>XHTML of a Kaku note without its back-link number.</summary>
    public static string NoteContent(XElement note)
    {
        var p = note.Descendants().FirstOrDefault(e => e.Name.LocalName == "p") ?? note;
        var nodes = p.Nodes().ToList();
        var back = nodes.OfType<XElement>().FirstOrDefault(e => HasClass(e, BackrefClass));
        if (back != null) nodes = nodes.SkipWhile(n => n != back).Skip(1).ToList();
        var html = string.Concat(nodes.Select(Strip));
        return html.Trim();
    }

    /// <summary>Serializes without namespace declarations (note content is re-inserted into other documents).</summary>
    private static string Strip(XNode n) => Regex.Replace(XmlFragmentWriter.Outer(n), @"\s+xmlns(:\w+)?=""[^""]*""", "");

    // ------------------------------------------------------------------ foreign notes

    private static void FindForeign(Book book, Dictionary<string, XDocument> docs, NoteIndexResult r)
    {
        foreach (var c in book.Chapters)
        {
            if (!docs.TryGetValue(c.Path, out var doc)) continue;
            var ex = TextExtractor.Extract(doc, c.Path);
            var blockOf = BlockLookup(ex);
            foreach (var a in doc.Descendants().Where(e => e.Name.LocalName == "a" && e.Attribute("href") != null && !HasClass(e, MarkerClass) && !HasClass(e, BackrefClass)))
            {
                var href = (string)a.Attribute("href")!;
                if (EpubPath.IsExternal(href)) continue;
                if (a.Ancestors().Any(x => HasType(x, "footnote", "endnote", "rearnote", "footnotes", "endnotes", "rearnotes") || HasClass(x, NoteClass))) continue;
                var (path, frag) = EpubPath.SplitHref(href);
                if (frag == null) continue;
                var target = path.Length == 0 ? c.Path : EpubPath.Resolve(c.Path, href);
                if (!docs.TryGetValue(target, out var tdoc)) continue;
                var te = tdoc.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == frag);
                if (te == null) continue;
                var text = a.Value.Trim();
                bool isNoteref = HasType(a, "noteref") || HasType(te, "footnote", "endnote", "rearnote")
                                 || te.Ancestors().Any(x => HasType(x, "footnote", "endnote", "rearnote"));
                bool looksLikeOne = text.Length is > 0 and <= 4 && (text.All(char.IsDigit) || text.All(ch => ch is '*' or '†' or '[' or ']' || char.IsDigit(ch)))
                                    && (a.Parent?.Name.LocalName == "sup" || a.Elements().Any(x => x.Name.LocalName == "sup") || HasClass(a, "noteref"));
                // a back-link in the note pointing to this anchor is not a note
                bool isBack = te.DescendantsAndSelf().Any(x => x.Name.LocalName == "a" && EpubPath.SplitHref((string?)x.Attribute("href") ?? "").Fragment == (string?)a.Attribute("id") && a.Attribute("id") != null)
                              && !isNoteref && c.Path != target && book.ChapterByPath(c.Path)!.Index > book.ChapterByPath(target)!.Index;
                if (!(isNoteref || looksLikeOne) || isBack) continue;
                var block = NoteBlockElement(te);
                var noteText = LeadingNumberRe().Replace(block?.Value.Trim() ?? te.Value.Trim(), "");
                int bi = blockOf(a);
                r.Foreign.Add(new ForeignNote(c.Path, bi, text, target, frag, noteText, Context(ex, bi, a)));
            }
        }
    }

    /// <summary>The element holding a foreign note's text: the target if it is a block, else its enclosing block.</summary>
    public static XElement? NoteBlockElement(XElement target)
    {
        string[] blocks = ["aside", "div", "p", "li", "dd", "section", "td"];
        for (var x = target; x != null; x = x.Parent)
        {
            if (x.Name.LocalName == "body") return null;
            if (blocks.Contains(x.Name.LocalName)) return x;
        }
        return null;
    }
}
