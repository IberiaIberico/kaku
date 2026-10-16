using System.Text;
using System.Xml.Linq;
using Kaku.Core.Editing;
using Kaku.Core.Epub;
using Kaku.Core.Notes;

namespace Kaku.Core.Structure;

/// <summary>
/// Notes inside the book. Markers are &lt;a epub:type="noteref" class="kaku-noteref"&gt; links; notes are
/// &lt;aside epub:type="footnote" id="kaku-nota-N" class="kaku-nota"&gt;. Every operation ends with a layout
/// pass that renumbers markers, rewrites links and back-links, and puts the note texts where the book
/// wants them (a "Notas" page, or the end of each chapter).
/// </summary>
public sealed partial class BookStructure
{
    private const string NotesCss = """
        /* Kaku: notas */
        a.kaku-noteref { vertical-align: super; font-size: 0.7em; line-height: 0; text-decoration: none; }
        .kaku-nota p { font-size: 0.9em; margin: 0.4em 0; text-indent: 0; }
        a.kaku-backref { text-decoration: none; font-weight: bold; }
        .kaku-notas-sep { width: 30%; margin: 2em auto 1em 0; }

        """;

    public NoteIndexResult Notes() => NoteIndex.Build(book);

    // ================================================================== operations

    /// <summary>Inserts a marker at a text position with a new note (<paramref name="reuseUid"/> = point to an existing note instead).</summary>
    public StructureChange InsertNote(Chapter chapter, int block, int offset, string contentXhtml, int? reuseUid = null) => Run(() =>
    {
        if (chapter.ParseError != null) throw new InvalidOperationException("El capítulo tiene XHTML no válido.");
        var idx = NoteIndex.Build(book);
        int uid = reuseUid ?? NextUid(idx);
        if (reuseUid != null && idx.Notes.All(n => n.Uid != reuseUid)) throw new InvalidOperationException("La nota ya no existe.");

        // the marker needs the epub prefix in scope before it is created
        if (EnsureEpubNamespace(chapter.SourceText) is { } withNs) chapter.Reload(withNs);
        var ns = chapter.Document!.Root!.Name.Namespace;
        var marker = new XElement(ns + "a",
            new XAttribute(XNamespace.Get(NoteIndex.OpsNs) + "type", "noteref"),
            new XAttribute("class", NoteIndex.MarkerClass),
            new XAttribute("href", "#" + NoteIndex.NoteId(uid)), "*");
        var editor = new ChapterEditor(book, chapter);
        if (!editor.InsertInlineElement(block, offset, marker)) throw new InvalidOperationException("No se puede poner una nota en esta posición.");
        if (reuseUid == null) AppendTempNote(chapter, uid, contentXhtml);
        book.FlushChapters();
        Layout(idx.Placement, idx.Style);
        return StructureChange.Simple(reuseUid == null ? "Insertar nota" : "Reutilizar nota");
    });

    public StructureChange EditNote(int uid, string contentXhtml) => Run(() =>
    {
        var (path, text, doc, el) = FindNoteElement(uid) ?? throw new InvalidOperationException("La nota ya no existe.");
        var r = RangeIn(text, doc, el);
        Put(path, text[..r.OuterStart] + TempNote(uid, contentXhtml) + text[r.OuterEnd..]);
        var idx = NoteIndex.Build(book, c => PackageText(c.Path));
        Layout(idx.Placement, idx.Style);
        return StructureChange.Simple("Editar nota");
    });

    /// <summary>Removes a note and all its markers.</summary>
    public StructureChange DeleteNote(int uid) => Run(() =>
    {
        var id = NoteIndex.NoteId(uid);
        foreach (var c in book.Chapters)
        {
            var text = PackageText(c.Path);
            if (!text.Contains(id, StringComparison.Ordinal)) continue;
            var doc = XmlLoader.Parse(text);
            var ranges = Ranges(text, doc);
            var cut = doc.Descendants()
                .Where(e => (string?)e.Attribute("id") == id
                            || e.Name.LocalName == "a" && NoteIndex.HasClass(e, NoteIndex.MarkerClass) && EpubPath.SplitHref((string?)e.Attribute("href") ?? "").Fragment == id)
                .Select(e => ranges[e]).OrderByDescending(r => r.OuterStart).ToList();
            var sb = new StringBuilder(text);
            foreach (var r in cut) sb.Remove(r.OuterStart, r.OuterEnd - r.OuterStart);
            Put(c.Path, sb.ToString());
        }
        var idx = NoteIndex.Build(book, c => PackageText(c.Path));
        Layout(idx.Placement, idx.Style);
        return StructureChange.Simple("Eliminar nota");
    });

    /// <summary>Where notes go and how markers look; rewrites every note.</summary>
    public StructureChange SetNoteSettings(NotePlacement placement, NoteMarkerStyle style) => Run(() =>
    {
        Layout(placement, style);
        return StructureChange.Simple("Formato de las notas");
    });

    /// <summary>Renumbers and repairs all notes (after edits in code mode, for example).</summary>
    public StructureChange RenumberNotes() => Run(() =>
    {
        var idx = NoteIndex.Build(book, c => PackageText(c.Path));
        Layout(idx.Placement, idx.Style);
        return StructureChange.Simple("Renumerar notas");
    });

    /// <summary>Turns foreign notes (publisher footnotes, superscript links) into Kaku notes.</summary>
    public StructureChange ConvertForeignNotes() => Run(() =>
    {
        var idx = NoteIndex.Build(book);
        if (idx.Foreign.Count == 0) return StructureChange.Simple("Convertir notas");
        int next = NextUid(idx);
        var uidOf = new Dictionary<(string, string?), int>();
        var docs = new Dictionary<string, (string Prolog, XDocument Doc)>();
        (string Prolog, XDocument Doc) Doc(string path)
        {
            if (docs.TryGetValue(path, out var d)) return d;
            var text = EnsureEpubNamespace(PackageText(path)) ?? PackageText(path);
            var x = XmlLoader.Parse(text);
            return docs[path] = (text[..XmlSourceScanner.Scan(text)[0].OuterStart], x);
        }
        var opsType = XNamespace.Get(NoteIndex.OpsNs) + "type";
        var removeTargets = new List<XElement>();
        foreach (var f in idx.Foreign)
        {
            if (!uidOf.TryGetValue((f.TargetPath, f.Fragment), out var uid))
            {
                uid = next++;
                uidOf[(f.TargetPath, f.Fragment)] = uid;
                var target = Doc(f.TargetPath).Doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == f.Fragment);
                var holder = target == null ? null : NoteIndex.NoteBlockElement(target);
                var content = holder == null ? SecurityText(f.Text) : ForeignContent(holder);
                var body = Doc(f.ChapterPath).Doc.Descendants().First(e => e.Name.LocalName == "body");
                body.Add(XElement.Parse(TempNote(uid, content).Replace("<aside ", $"<aside xmlns=\"{body.Name.NamespaceName}\" xmlns:epub=\"{NoteIndex.OpsNs}\" ")));
                if (holder != null) removeTargets.Add(holder);
            }
            // replace the anchor (and a <sup> that only wraps it) by a Kaku marker
            var (_, cdoc) = Doc(f.ChapterPath);
            var a = cdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "a" && !NoteIndex.HasClass(e, NoteIndex.MarkerClass)
                && EpubPath.SplitHref((string?)e.Attribute("href") ?? "").Fragment == f.Fragment
                && (EpubPath.SplitHref((string?)e.Attribute("href") ?? "").Path is var p && (p.Length == 0 ? f.ChapterPath : EpubPath.Resolve(f.ChapterPath, (string)e.Attribute("href")!)) == f.TargetPath));
            if (a == null) continue;
            var ns = cdoc.Root!.Name.Namespace;
            var marker = new XElement(ns + "a", new XAttribute(opsType, "noteref"), new XAttribute("class", NoteIndex.MarkerClass),
                new XAttribute("href", "#" + NoteIndex.NoteId(uid)), "*");
            var replace = a.Parent is { } sup && sup.Name.LocalName == "sup" && sup.Nodes().All(n => n == a || n is XText t && t.Value.Trim().Length == 0) ? sup : a;
            replace.ReplaceWith(marker);
        }
        foreach (var t in removeTargets)
        {
            var parent = t.Parent;
            t.Remove();
            // drop containers left empty (aside/div/section of the old notes)
            while (parent != null && parent.Name.LocalName is "aside" or "div" or "section" or "ol" or "ul" && !parent.Elements().Any() && parent.Value.Trim().Length == 0)
            {
                var up = parent.Parent;
                parent.Remove();
                parent = up;
            }
        }
        foreach (var (path, (prolog, doc)) in docs)
        {
            var sb = new StringBuilder(prolog);
            XmlFragmentWriter.Write(sb, doc.Root!);
            sb.Append('\n');
            Put(path, sb.ToString());
        }
        var after = NoteIndex.Build(book, c => PackageText(c.Path));
        Layout(after.Placement, after.Style);
        return StructureChange.Simple("Convertir notas");
    });

    private static string SecurityText(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    /// <summary>Inner XHTML of a foreign note, without its number and back-link.</summary>
    private static string ForeignContent(XElement holder)
    {
        var p = holder.Name.LocalName is "aside" or "div" or "section" ? holder.Descendants().FirstOrDefault(e => e.Name.LocalName == "p") ?? holder : holder;
        var clone = new XElement(p);
        // the first link (back to the text) and a leading "1." go
        var firstLink = clone.Descendants().FirstOrDefault(e => e.Name.LocalName == "a");
        if (firstLink != null && firstLink.Value.Trim().Length <= 6) firstLink.Remove();
        foreach (var sup in clone.Descendants().Where(e => e.Name.LocalName == "sup" && e.Value.Trim().Length <= 4).ToList()) sup.Remove();
        var html = string.Concat(clone.Nodes().Select(n => System.Text.RegularExpressions.Regex.Replace(XmlFragmentWriter.Outer(n), @"\s+xmlns(:\w+)?=""[^""]*""", "")));
        return System.Text.RegularExpressions.Regex.Replace(html.Trim(), @"^\s*[\[(]?\d+[\])]?\s*[.:]?\s*", "");
    }

    // ================================================================== layout pass

    private static bool Under(Chapter c, Chapter root)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p == root) return true;
        return false;
    }

    private string PackageText(string path) => XmlLoader.DecodeText(book.Package.Read(path)!);

    /// <summary>A note id never used in the book (orphan markers keep theirs reserved).</summary>
    private int NextUid(NoteIndexResult idx)
    {
        int max = idx.Notes.Select(n => n.Uid).DefaultIfEmpty(0).Max();
        foreach (var c in book.Chapters)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(c.SourceText, @"kaku-nota-(\d+)"))
                max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max + 1;
    }

    private bool IsEpub3 => book.Opf.Version.StartsWith('3');

    private string TempNote(int uid, string content) =>
        $"<aside epub:type=\"footnote\" id=\"{NoteIndex.NoteId(uid)}\" class=\"{NoteIndex.NoteClass}\"><p>{content}</p></aside>";

    private void AppendTempNote(Chapter chapter, int uid, string content)
    {
        var text = chapter.SourceText;
        var doc = XmlLoader.Parse(text);
        var body = doc.Descendants().First(e => e.Name.LocalName == "body");
        var r = RangeIn(text, doc, body);
        chapter.Reload(text[..r.ContentEnd] + TempNote(uid, content) + "\n" + text[r.ContentEnd..]);
    }

    private (string Path, string Text, XDocument Doc, XElement Element)? FindNoteElement(int uid)
    {
        var id = NoteIndex.NoteId(uid);
        foreach (var c in book.Chapters)
        {
            var text = PackageText(c.Path);
            if (!text.Contains(id, StringComparison.Ordinal)) continue;
            var doc = XmlLoader.Parse(text);
            if (doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == id) is { } el) return (c.Path, text, doc, el);
        }
        return null;
    }

    private static Dictionary<XElement, ElementRange> Ranges(string text, XDocument doc) =>
        doc.Root!.DescendantsAndSelf().Zip(XmlSourceScanner.Scan(text)).ToDictionary(p => p.First, p => p.Second);

    private static ElementRange RangeIn(string text, XDocument doc, XElement e) => Ranges(text, doc)[e];

    /// <summary>Adds xmlns:epub to the root element if missing. Returns null if nothing changed.</summary>
    private static string? EnsureEpubNamespace(string text)
    {
        var doc = XmlLoader.Parse(text);
        var root = doc.Root!;
        if (root.GetPrefixOfNamespace(NoteIndex.OpsNs) == "epub") return null;
        var r = XmlSourceScanner.Scan(text)[0];
        int at = r.ContentStart - (r.SelfClosing ? 2 : 1);
        return text[..at] + $" xmlns:epub=\"{NoteIndex.OpsNs}\"" + text[at..];
    }

    private string EnsureNotesCss()
    {
        var existing = book.Package.Entries.FirstOrDefault(e => EpubPath.FileName(e.Path) == "kaku-notas.css")?.Path;
        if (existing != null) return existing;
        var path = EpubPath.Unique(FolderFor("kaku-notas.css"), "kaku-notas.css", book.Package.Exists);
        Put(path, NotesCss);
        var opf = Opf();
        opf.AddItem(path, "text/css");
        opf.Save();
        return path;
    }

    /// <summary>The layout pass: numbers, links, back-links and the placement of every Kaku note.</summary>
    private void Layout(NotePlacement placement, NoteMarkerStyle style)
    {
        var idx = NoteIndex.Build(book, c => PackageText(c.Path));
        idx.Placement = placement;
        NoteIndex.Number(idx);
        var notes = idx.Notes.Where(n => n.Markers.Count > 0).ToList(); // a note nobody refers to is dropped
        var opf = Opf();
        opf.SetMeta(NoteIndex.MetaPlacement, placement == NotePlacement.EndOfChapter ? "chapter" : "page");
        opf.SetMeta(NoteIndex.MetaStyle, style switch { NoteMarkerStyle.Brackets => "brackets", NoteMarkerStyle.Asterisk => "asterisk", _ => "numbers" });

        // the notes page: created on demand, removed when unused
        string? page = idx.NotesPagePath ?? opf.GetMeta(NoteIndex.MetaPage);
        if (page != null && !book.Package.Exists(page)) page = null;
        bool needPage = placement == NotePlacement.NotesPage && notes.Count > 0;
        if (needPage && page == null)
        {
            // after the main text (before colophon / publisher pages), styled like a text chapter
            var last = book.Chapters.LastOrDefault(c => c.Linear && c.WordCount > 200 && !c.IsImageOnly)
                       ?? book.Chapters.LastOrDefault(c => c.Linear) ?? book.Chapters.Last();
            // never inside a chapter's group: after its last part
            var root = last;
            while (root.Parent != null) root = root.Parent;
            int end = last.Index;
            while (end + 1 < book.Chapters.Count && Under(book.Chapters[end + 1], root)) end++;
            var after = book.Chapters[end];
            page = EpubPath.Unique(EpubPath.DirectoryOf(last.Path), "notas.xhtml", book.Package.Exists);
            Put(page, BuildXhtml(last, page, "Notas", "<h1>Notas</h1>"));
            opf.AddItem(page, "application/xhtml+xml");
            opf.InsertInSpine(page, opf.SpinePaths().IndexOf(after.Path) + 1);
            opf.SetMeta(NoteIndex.MetaPage, page);
            opf.TouchModified();
            opf.Save();
            book.Opf = OpfDocument.Load(book.Package);
            var toc = book.Toc.ToList();
            var later = book.Chapters.Skip(after.Index + 1).Select(c => c.Path).ToHashSet();
            int pos = toc.FindIndex(e => later.Contains(e.Path));
            toc.Insert(pos < 0 ? toc.Count : pos, new TocEntry("Notas", page, null, 0));
            TocWriter.Write(book, toc);
            opf = Opf();
        }
        else if (!needPage && page != null)
        {
            opf.RemoveItem(page);
            opf.SetMeta(NoteIndex.MetaPage, null);
            opf.Save();
            book.Package.Remove(page);
            book.Opf = OpfDocument.Load(book.Package);
            if (book.Toc.Any(t => t.Path == page)) TocWriter.Write(book, book.Toc.Where(t => t.Path != page).ToList());
            opf = Opf();
            page = null;
        }
        else
        {
            opf.Save();
        }

        var css = notes.Count > 0 ? EnsureNotesCss() : null;
        var noteHome = notes.ToDictionary(n => n.Uid, n => placement == NotePlacement.NotesPage ? page! : n.Markers[0].ChapterPath);

        foreach (var c in book.Chapters.Where(c => c.Path != page && book.Package.Exists(c.Path)))
        {
            var text = PackageText(c.Path);
            bool mentions = text.Contains("kaku-nota", StringComparison.Ordinal) || text.Contains(NoteIndex.MarkerClass, StringComparison.Ordinal);
            var homeNotes = notes.Where(n => noteHome[n.Uid] == c.Path).ToList();
            if (!mentions && homeNotes.Count == 0) continue;
            Put(c.Path, LayoutChapter(c.Path, text, notes, noteHome, homeNotes, style, css));
        }
        if (page != null)
        {
            var text = PackageText(page);
            var doc = XmlLoader.Parse(text);
            var body = doc.Descendants().First(e => e.Name.LocalName == "body");
            var r = RangeIn(text, doc, body);
            var sb = new StringBuilder("\n<h1>Notas</h1>\n");
            sb.Append(IsEpub3 ? "<section epub:type=\"footnotes\" class=\"kaku-notas\">\n" : "<div class=\"kaku-notas\">\n");
            foreach (var n in notes) sb.Append(NoteElement(n, page, style)).Append('\n');
            sb.Append(IsEpub3 ? "</section>\n" : "</div>\n");
            var updated = text[..r.ContentStart] + sb + text[r.ContentEnd..];
            updated = EnsureEpubNamespace(updated) ?? updated;
            if (css != null) updated = EnsureCssLink(page, updated, css);
            Put(page, updated);
        }
    }

    private string NoteElement(BookNote n, string notePath, NoteMarkerStyle style)
    {
        var first = n.Markers[0];
        var back = first.ChapterPath == notePath ? "#" + NoteIndex.RefId(n.Uid, 1) : EpubPath.Relative(notePath, first.ChapterPath, NoteIndex.RefId(n.Uid, 1));
        var tag = IsEpub3 ? "aside" : "div";
        var label = style == NoteMarkerStyle.Asterisk ? NoteIndex.MarkerText(style, n.Number) : n.Number + ".";
        return $"<{tag} epub:type=\"footnote\" id=\"{n.Id}\" class=\"{NoteIndex.NoteClass}\"><p><a class=\"{NoteIndex.BackrefClass}\" href=\"{Attr(back)}\">{label}</a> {n.ContentXhtml}</p></{tag}>";
    }

    private static string Attr(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;");

    private string LayoutChapter(string path, string text, List<BookNote> notes, Dictionary<int, string> noteHome,
        List<BookNote> homeNotes, NoteMarkerStyle style, string? css)
    {
        var doc = XmlLoader.Parse(text);
        var ranges = Ranges(text, doc);
        var byUid = notes.ToDictionary(n => n.Uid);
        var edits = new List<(int Start, int End, string Text)>();
        var removedRoots = new HashSet<XElement>();

        // old note containers and notes: removed (they are written again below or on the notes page)
        foreach (var e in doc.Descendants().ToList())
        {
            if (e.Ancestors().Any(removedRoots.Contains)) continue;
            bool section = NoteIndex.HasClass(e, NoteIndex.SectionClass);
            bool note = NoteIndex.UidOf((string?)e.Attribute("id")) != null;
            if (!section && !note) continue;
            removedRoots.Add(e);
            var r = ranges[e];
            int start = r.OuterStart;
            while (start > 0 && text[start - 1] is ' ' or '\t') start--;
            if (start > 0 && text[start - 1] == '\n') start--;
            edits.Add((start, r.OuterEnd, ""));
        }

        // markers: number, id, link
        var seen = new Dictionary<int, int>();
        foreach (var a in doc.Descendants().Where(e => e.Name.LocalName == "a" && NoteIndex.HasClass(e, NoteIndex.MarkerClass)))
        {
            if (a.Ancestors().Any(removedRoots.Contains)) continue;
            var uid = NoteIndex.UidOf(EpubPath.SplitHref((string?)a.Attribute("href") ?? "").Fragment);
            var r = ranges[a];
            if (uid is not { } u || !byUid.TryGetValue(u, out var n))
            {
                edits.Add((r.OuterStart, r.OuterEnd, "")); // marker of a deleted note
                continue;
            }
            int k = n.Markers.FindIndex(m => m.ChapterPath == path && m.ElementId == (string?)a.Attribute("id"));
            if (k < 0) k = n.Markers.FindIndex(m => m.ChapterPath == path);
            k = Math.Max(0, k) + 1;
            if (seen.TryGetValue(u, out var used) && used >= k) k = used + 1;
            seen[u] = k;
            var home = noteHome[u];
            var href = home == path ? "#" + n.Id : EpubPath.Relative(path, home, n.Id);
            edits.Add((r.OuterStart, r.OuterEnd,
                $"<a epub:type=\"noteref\" class=\"{NoteIndex.MarkerClass}\" id=\"{NoteIndex.RefId(u, k)}\" href=\"{Attr(href)}\">{NoteIndex.MarkerText(style, n.Number)}</a>"));
        }

        // notes at the end of this chapter
        if (homeNotes.Count > 0)
        {
            var body = doc.Descendants().First(e => e.Name.LocalName == "body");
            var sb = new StringBuilder();
            sb.Append(IsEpub3 ? "\n<section epub:type=\"footnotes\" class=\"kaku-notas\">\n" : "\n<div class=\"kaku-notas\">\n");
            sb.Append("<hr class=\"kaku-notas-sep\"/>\n");
            foreach (var n in homeNotes) sb.Append(NoteElement(n, path, style)).Append('\n');
            sb.Append(IsEpub3 ? "</section>\n" : "</div>\n");
            edits.Add((ranges[body].ContentEnd, ranges[body].ContentEnd, sb.ToString()));
        }

        var result = new StringBuilder(text);
        foreach (var (s, e, t) in edits.OrderByDescending(x => x.Start).ThenByDescending(x => x.End))
            result.Remove(s, e - s).Insert(s, t);
        var updated = result.ToString();
        bool hasNotes = updated.Contains(NoteIndex.MarkerClass, StringComparison.Ordinal) || homeNotes.Count > 0;
        if (hasNotes)
        {
            updated = EnsureEpubNamespace(updated) ?? updated;
            if (css != null) updated = EnsureCssLink(path, updated, css);
        }
        return updated;
    }

    private static string EnsureCssLink(string path, string text, string css)
    {
        var href = EpubPath.Relative(path, css);
        if (text.Contains(href, StringComparison.Ordinal)) return text;
        var doc = XmlLoader.Parse(text);
        var head = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "head");
        if (head == null) return text;
        var r = RangeIn(text, doc, head);
        if (r.SelfClosing) return text;
        return text[..r.ContentEnd] + $"<link rel=\"stylesheet\" type=\"text/css\" href=\"{Attr(href)}\"/>\n" + text[r.ContentEnd..];
    }
}
