using System.Security;
using System.Text;
using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Structure;

/// <summary>Blocks [FromBlock..] of FromPath now live in ToPath starting at ToBlockOffset (split/merge).</summary>
public sealed record BlockMove(string FromPath, int FromBlock, string ToPath, int ToBlockOffset)
{
    public BlockMove Inverse => new(ToPath, ToBlockOffset, FromPath, FromBlock);
}

/// <summary>What a structural operation did, so the UI can follow (tabs, bookmarks, selection).</summary>
public sealed record StructureChange(
    string Label,
    Dictionary<string, string> Renamed,
    HashSet<string> Removed,
    List<BlockMove> Moves,
    string? Focus,
    int FocusBlock = 0)
{
    /// <summary>Chapters whose text was reloaded (their text undo history no longer applies).</summary>
    public List<Chapter> Reloaded { get; set; } = new();

    public static StructureChange Simple(string label, string? focus = null) => new(label, new(), new(), new(), focus);
}

/// <summary>A TOC row being edited; BlockIndex asks for an id on that block when the row is applied.</summary>
public sealed record TocDraft(string Title, string Path, string? Fragment, int Level, int BlockIndex = -1);

public enum PageTemplate { Blank, TranslatorNote, Characters }

/// <summary>
/// Book-level operations: pages (add, import, delete, duplicate, rename, reorder, split, merge), files
/// (add, replace, delete), cover, metadata and table of contents. Every operation keeps links, the
/// manifest, the spine and the TOC consistent, and can be undone (<see cref="Undo"/>).
/// </summary>
public sealed partial class BookStructure(Book book)
{
    private readonly Stack<(List<EpubEntry> Snapshot, StructureChange Change)> _undo = new();
    private readonly Stack<(List<EpubEntry> Snapshot, StructureChange Change)> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo.Peek().Change.Label : null;

    // ================================================================== infrastructure

    private StructureChange Run(Func<StructureChange> op)
    {
        book.FlushChapters();
        var snapshot = book.Package.Snapshot();
        StructureChange change;
        try
        {
            change = op();
        }
        catch
        {
            book.Package.Restore(snapshot);
            book.RebuildModel();
            throw;
        }
        change.Reloaded = book.RebuildModel(change.Renamed);
        _undo.Push((snapshot, change));
        _redo.Clear();
        return change;
    }

    /// <summary>Restores the package as it was before the last operation. Returns the inverse change.</summary>
    public StructureChange? Undo() => Step(_undo, _redo);

    public StructureChange? Redo() => Step(_redo, _undo);

    private StructureChange? Step(Stack<(List<EpubEntry>, StructureChange)> from, Stack<(List<EpubEntry>, StructureChange)> to)
    {
        if (from.Count == 0) return null;
        book.FlushChapters();
        var (snapshot, change) = from.Pop();
        var current = book.Package.Snapshot();
        book.Package.Restore(snapshot);
        var inverse = new StructureChange(change.Label,
            change.Renamed.ToDictionary(kv => kv.Value, kv => kv.Key),
            new HashSet<string>(current.Select(e => e.Path).Except(snapshot.Select(e => e.Path))),
            change.Moves.Select(m => m.Inverse).Reverse().ToList(), change.Focus);
        inverse.Reloaded = book.RebuildModel(inverse.Renamed);
        to.Push((current, inverse));
        return inverse;
    }

    private OpfEditor Opf() => new(book.Package, book.Opf.Path);

    private void Put(string path, string text) => book.Package.Put(path, new UTF8Encoding(false).GetBytes(text));

    private string TextOf(string path) => book.ChapterByPath(path)?.SourceText ?? XmlLoader.DecodeText(book.Package.Read(path)!);

    private void ApplyRewrites(Dictionary<string, string> changed)
    {
        foreach (var (path, text) in changed) Put(path, text);
    }

    /// <summary>Reading-order position of a file among all spine entries (OPF order).</summary>
    private static int SpineIndex(OpfEditor opf, string path) => opf.SpinePaths().IndexOf(path);

    public static string MediaTypeFor(string path) => EpubPath.Extension(path) switch
    {
        ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
        ".css" => "text/css",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".js" => "text/javascript",
        ".ncx" => "application/x-dtbncx+xml",
        ".xml" => "application/xml",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>Every reference to a file (or to the fragments in it).</summary>
    public List<LinkRef> UsagesOf(string path) => LinkIndex.Build(book).Where(l => l.Target == path && l.Source != path).ToList();

    // ================================================================== pages

    /// <summary>XHTML body for a template page.</summary>
    public static string TemplateBody(PageTemplate t, string title) => t switch
    {
        PageTemplate.TranslatorNote => $"<h1>{Esc(title)}</h1>\n<p>Esta traducción…</p>",
        PageTemplate.Characters => $"<h1>{Esc(title)}</h1>\n<p><b>Nombre</b>: descripción del personaje.</p>\n<p><b>Nombre</b>: descripción del personaje.</p>",
        _ => $"<h1>{Esc(title)}</h1>\n<p></p>",
    };

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>
    /// Adds a new XHTML page at reading-order position <paramref name="index"/> (0 = first chapter), styled like
    /// its neighbour (same stylesheets, html/body classes, language). Optionally adds it to the TOC.
    /// </summary>
    public StructureChange AddPage(string title, string bodyXhtml, int index, bool addToToc) => Run(() =>
    {
        var neighbour = book.Chapters.Count == 0 ? null : book.Chapters[Math.Clamp(index - 1, 0, book.Chapters.Count - 1)];
        var dir = neighbour != null ? EpubPath.DirectoryOf(neighbour.Path) : EpubPath.DirectoryOf(book.Opf.Path);
        var path = EpubPath.Unique(dir, EpubPath.Slug(title) + ".xhtml", book.Package.Exists);
        Put(path, BuildXhtml(neighbour, path, title, bodyXhtml));

        var opf = Opf();
        opf.AddItem(path, "application/xhtml+xml");
        var spine = opf.SpinePaths();
        int at = index >= book.Chapters.Count ? spine.Count : Math.Max(0, spine.IndexOf(book.Chapters[index].Path));
        opf.InsertInSpine(path, at);
        opf.TouchModified();
        opf.Save();
        book.Opf = OpfDocument.Load(book.Package);

        if (addToToc)
        {
            // keep the TOC in reading order: insert before the first entry of a later chapter
            var later = book.Chapters.Skip(index).Select(c => c.Path).ToHashSet();
            var toc = book.Toc.ToList();
            int pos = toc.FindIndex(e => later.Contains(e.Path));
            if (pos < 0) pos = toc.Count;
            toc.Insert(pos, new TocEntry(title, path, null, 0));
            TocWriter.Write(book, toc);
        }
        return StructureChange.Simple("Añadir página", path);
    });

    /// <summary>A complete XHTML document for a new page, copying head links and classes from a neighbour chapter.</summary>
    private string BuildXhtml(Chapter? neighbour, string path, string title, string bodyXhtml)
    {
        var lang = book.Opf.Language.Length > 0 ? book.Opf.Language : "es";
        var links = new StringBuilder();
        string htmlClass = "", bodyClass = "";
        if (neighbour?.Document?.Root is { } root)
        {
            foreach (var l in root.Descendants().Where(e => e.Name.LocalName == "link" && (string?)e.Attribute("rel") == "stylesheet"))
                if ((string?)l.Attribute("href") is { } href && !EpubPath.IsExternal(href))
                    links.Append($"\n<link rel=\"stylesheet\" type=\"text/css\" href=\"{Esc(EpubPath.Relative(path, EpubPath.Resolve(neighbour.Path, href)))}\"/>");
            htmlClass = (string?)root.Attribute("class") ?? "";
            bodyClass = (string?)root.Descendants().FirstOrDefault(e => e.Name.LocalName == "body")?.Attribute("class") ?? "";
        }
        bool epub3 = book.Opf.Version.StartsWith('3');
        var doctype = epub3 ? "<!DOCTYPE html>" : "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">";
        var epubNs = epub3 ? " xmlns:epub=\"http://www.idpf.org/2007/ops\"" : "";
        var hc = htmlClass.Length > 0 ? $" class=\"{Esc(htmlClass)}\"" : "";
        var bc = bodyClass.Length > 0 ? $" class=\"{Esc(bodyClass)}\"" : "";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            {doctype}
            <html xmlns="http://www.w3.org/1999/xhtml"{epubNs} xml:lang="{Esc(lang)}" lang="{Esc(lang)}"{hc}>
            <head>
            <meta charset="UTF-8"/>
            <title>{Esc(title)}</title>{links}
            </head>
            <body{bc}>
            {bodyXhtml}
            </body>
            </html>

            """;
    }

    /// <summary>Converts an imported file to an XHTML body: TXT/Markdown paragraphs, or the body of an (X)HTML file.</summary>
    public static (string Title, string Body) ImportBody(string fileName, string content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(fileName);
        if (ext is ".xhtml" or ".html" or ".htm")
        {
            try
            {
                var doc = XmlLoader.Parse(content);
                var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
                var h = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "h1" or "h2" or "title" && e.Value.Trim().Length > 0);
                if (h != null) title = h.Value.Trim();
                if (body != null) return (title, StripNamespaces(XmlFragmentWriter.Inner(body)));
            }
            catch (System.Xml.XmlException)
            {
                // not well-formed HTML: fall back to its text
                content = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", "\n");
                content = System.Net.WebUtility.HtmlDecode(content);
            }
        }
        var sb = new StringBuilder();
        var para = new List<string>();
        void Flush()
        {
            if (para.Count == 0) return;
            sb.Append("<p>").Append(Inline(string.Join(' ', para))).Append("</p>\n");
            para.Clear();
        }
        bool markdown = ext is ".md" or ".markdown";
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (markdown && line.StartsWith('#'))
            {
                Flush();
                int level = Math.Clamp(line.TakeWhile(c => c == '#').Count(), 1, 6);
                var text = line.TrimStart('#').Trim();
                if (sb.Length == 0 && level == 1) title = text;
                sb.Append($"<h{level}>").Append(Inline(text)).Append($"</h{level}>\n");
                continue;
            }
            if (markdown) para.Add(line);
            else
            {
                para.Add(line); // plain text: each line is a paragraph
                Flush();
            }
        }
        Flush();
        return (title, sb.ToString().TrimEnd());

        string Inline(string s)
        {
            var e = Esc(s);
            if (!markdown) return e;
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\*\*(.+?)\*\*", "<b>$1</b>");
            e = System.Text.RegularExpressions.Regex.Replace(e, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<i>$1</i>");
            return e;
        }
    }

    private static string StripNamespaces(string xhtml) =>
        System.Text.RegularExpressions.Regex.Replace(xhtml, @"\s+xmlns(:\w+)?=""[^""]*""", "");

    /// <summary>Deletes files (pages or any other file). TOC entries for deleted pages are removed.</summary>
    public StructureChange DeleteFiles(IReadOnlyCollection<string> paths) => Run(() =>
    {
        var set = paths.ToHashSet();
        var opf = Opf();
        foreach (var p in set) opf.RemoveItem(p);
        opf.TouchModified();
        opf.Save();
        foreach (var p in set) book.Package.Remove(p);
        book.Opf = OpfDocument.Load(book.Package);
        if (book.Toc.Any(e => set.Contains(e.Path))) TocWriter.Write(book, book.Toc.Where(e => !set.Contains(e.Path)).ToList());
        return new StructureChange(set.Count == 1 ? "Eliminar" : $"Eliminar {set.Count} archivos", new(), set, new(), null);
    });

    public StructureChange Duplicate(string path) => Run(() =>
    {
        var dir = EpubPath.DirectoryOf(path);
        var stem = Path.GetFileNameWithoutExtension(EpubPath.FileName(path));
        var copy = EpubPath.Unique(dir, stem + "-copia" + EpubPath.Extension(path), book.Package.Exists);
        book.Package.Put(copy, book.Package.Read(path)!);
        var opf = Opf();
        opf.AddItem(copy, MediaTypeFor(copy));
        int at = SpineIndex(opf, path);
        if (at >= 0) opf.InsertInSpine(copy, at + 1);
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple("Duplicar", copy);
    });

    /// <summary>Renames a file in its folder; every link, the manifest and the TOC follow.</summary>
    public StructureChange Rename(string path, string newFileName) => Run(() =>
    {
        newFileName = newFileName.Trim();
        if (newFileName.Length == 0 || newFileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '#']) >= 0)
            throw new ArgumentException("Nombre de archivo no válido.");
        if (!Path.HasExtension(newFileName)) newFileName += EpubPath.Extension(path);
        var newPath = EpubPath.DirectoryOf(path) + newFileName;
        if (newPath == path) return StructureChange.Simple("Renombrar", path);
        if (book.Package.Exists(newPath)) throw new ArgumentException("Ya existe un archivo con ese nombre.");

        var links = LinkIndex.Build(book);
        var renames = new Dictionary<string, string> { [path] = newPath };
        ApplyRewrites(LinkIndex.Rewrite(book, links, (t, f) => t == path ? (newPath, f) : null, renames));
        book.Package.Rename(path, newPath);
        var opf = Opf();
        opf.RenameItem(path, newPath);
        if (EpubPath.Extension(newPath) != EpubPath.Extension(path)) opf.SetMediaType(newPath, MediaTypeFor(newPath));
        opf.TouchModified();
        opf.Save();
        return new StructureChange("Renombrar", renames, new(), new(), newPath);
    });

    /// <summary>Moves a page in the reading order, before <paramref name="beforePath"/> (null = to the end).</summary>
    public StructureChange Move(string path, string? beforePath) => Move([path], beforePath);

    /// <summary>Moves several pages (kept in the given order, e.g. a chapter and its parts) before <paramref name="beforePath"/>.</summary>
    public StructureChange Move(IReadOnlyList<string> paths, string? beforePath) => Run(() =>
    {
        if (beforePath != null && paths.Contains(beforePath)) return StructureChange.Simple("Mover página", paths[0]);
        var opf = Opf();
        foreach (var path in paths)
        {
            var spine = opf.SpinePaths();
            spine.Remove(path);
            int at = beforePath == null ? spine.Count : Math.Max(0, spine.IndexOf(beforePath));
            opf.MoveInSpine(path, at);
        }
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple(paths.Count == 1 ? "Mover página" : "Mover capítulo", paths[0]);
    });

    /// <summary>Reorders TOC entries to follow the reading order (stable within a file).</summary>
    public StructureChange SortTocByReadingOrder() => Run(() =>
    {
        var order = book.Chapters.Select((c, i) => (c.Path, i)).ToDictionary(x => x.Path, x => x.i);
        var sorted = book.Toc.Select((e, i) => (e, i))
            .OrderBy(x => order.GetValueOrDefault(x.e.Path, int.MaxValue))
            .ThenBy(x => x.e.Fragment == null ? -1 : book.ChapterByPath(x.e.Path)?.IdToBlock.GetValueOrDefault(x.e.Fragment, 0) ?? 0)
            .ThenBy(x => x.i).Select(x => x.e).ToList();
        TocWriter.Write(book, sorted);
        return StructureChange.Simple("Ordenar índice");
    });

    // ================================================================== split / merge

    /// <summary>Splits a chapter before block <paramref name="block"/> into a new file right after it.</summary>
    public StructureChange Split(Chapter chapter, int block) => Run(() =>
    {
        if (chapter.ParseError != null) throw new InvalidOperationException("El capítulo tiene XHTML no válido.");
        if (block <= 0 || block >= chapter.Blocks.Count) throw new InvalidOperationException("Pon el cursor en un párrafo que no sea el primero.");
        if (chapter.Blocks[block].IsAnonymous || chapter.Blocks[block].IsNote) throw new InvalidOperationException("No se puede dividir en este punto; prueba en otro párrafo.");
        var src = chapter.SourceText;
        var prolog = src[..XmlSourceScanner.Scan(src)[0].OuterStart];
        var docA = XmlLoader.Parse(src);
        var docB = XmlLoader.Parse(src);
        var ea = TextExtractor.Extract(docA, chapter.Path).Blocks[block].Element!;
        var eb = TextExtractor.Extract(docB, chapter.Path).Blocks[block].Element!;
        var bodyA = docA.Descendants().First(e => e.Name.LocalName == "body");
        var bodyB = docB.Descendants().First(e => e.Name.LocalName == "body");

        // A keeps everything before the split point, B everything from it on
        foreach (var n in ea.NodesAfterSelf().ToList()) n.Remove();
        var parentA = ea.Parent!;
        ea.Remove();
        for (var p = parentA; p != bodyA && p != null; p = p.Parent) foreach (var n in p.NodesAfterSelf().ToList()) n.Remove();
        foreach (var n in eb.NodesBeforeSelf().ToList()) n.Remove();
        for (var p = eb.Parent; p != bodyB && p != null; p = p.Parent)
        {
            foreach (var n in p.NodesBeforeSelf().ToList()) n.Remove();
            p.Attribute("id")?.Remove(); // the copy of a container must not repeat its id
        }
        TrimTrailingWhitespace(bodyA);
        if (bodyB.FirstNode is XText lead && lead.Value.Trim().Length == 0) lead.Value = "\n";

        var dir = EpubPath.DirectoryOf(chapter.Path);
        var stem = Path.GetFileNameWithoutExtension(EpubPath.FileName(chapter.Path));
        var newPath = EpubPath.Unique(dir, stem + "-2" + EpubPath.Extension(chapter.Path), book.Package.Exists);
        var movedIds = eb.DescendantsAndSelf().Concat(eb.ElementsAfterSelf().SelectMany(x => x.DescendantsAndSelf()))
            .Concat(eb.Ancestors().TakeWhile(a => a != bodyB).SelectMany(a => a.ElementsAfterSelf().SelectMany(x => x.DescendantsAndSelf())))
            .Select(e => (string?)e.Attribute("id")).OfType<string>().ToHashSet();

        Put(chapter.Path, Serialize(prolog, docA));
        Put(newPath, Serialize(prolog, docB));
        RewriteAll((t, f) =>
            t == chapter.Path && f != null && movedIds.Contains(f) ? (newPath, f)
            : t == newPath && f != null && !movedIds.Contains(f) ? (chapter.Path, f)
            : null);

        var opf = Opf();
        var props = (string?)opf.ItemFor(chapter.Path)?.Attribute("properties");
        opf.AddItem(newPath, chapter.Item.MediaType, props);
        opf.InsertInSpine(newPath, SpineIndex(opf, chapter.Path) + 1);
        opf.TouchModified();
        opf.Save();
        return new StructureChange("Dividir capítulo", new(), new(), [new BlockMove(chapter.Path, block, newPath, 0)], newPath);
    });

    /// <summary>Appends the next chapter in reading order to this one and deletes its file; links follow.</summary>
    public StructureChange MergeWithNext(Chapter chapter) => Run(() =>
    {
        if (chapter.Index + 1 >= book.Chapters.Count) throw new InvalidOperationException("No hay un capítulo siguiente.");
        var next = book.Chapters[chapter.Index + 1];
        if (chapter.ParseError != null || next.ParseError != null) throw new InvalidOperationException("Uno de los capítulos tiene XHTML no válido.");

        // links inside the next chapter must work from this chapter's folder
        if (EpubPath.DirectoryOf(next.Path) != EpubPath.DirectoryOf(chapter.Path))
            ApplyRewrites(LinkIndex.Rewrite(book, LinkIndex.Build(book, next.Path), (_, _) => null, new Dictionary<string, string> { [next.Path] = chapter.Path }));

        var srcA = TextOf(chapter.Path);
        var prolog = srcA[..XmlSourceScanner.Scan(srcA)[0].OuterStart];
        var docA = XmlLoader.Parse(srcA);
        var docB = XmlLoader.Parse(TextOf(next.Path));
        var bodyA = docA.Descendants().First(e => e.Name.LocalName == "body");
        var bodyB = docB.Descendants().First(e => e.Name.LocalName == "body");

        // ids of the next chapter that already exist here get a new name
        var idsA = docA.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>().ToHashSet();
        var idMap = new Dictionary<string, string>();
        foreach (var e in bodyB.Descendants().Where(e => e.Attribute("id") != null))
        {
            var id = (string)e.Attribute("id")!;
            if (!idsA.Contains(id)) continue;
            var nid = id;
            for (int n = 2; idsA.Contains(nid) || idMap.ContainsValue(nid); n++) nid = $"{id}-{n}";
            idMap[id] = nid;
            e.SetAttributeValue("id", nid);
        }
        // a link to the next file itself now lands on its first element
        var first = bodyB.Elements().FirstOrDefault();
        string? anchor = null;
        if (first != null)
        {
            anchor = (string?)first.Attribute("id");
            if (anchor == null)
            {
                anchor = "kaku-merge-" + EpubPath.Slug(Path.GetFileNameWithoutExtension(next.Path));
                while (idsA.Contains(anchor)) anchor += "-x";
                first.SetAttributeValue("id", anchor);
            }
        }
        TrimTrailingWhitespace(bodyA);
        bodyA.Add(new XText("\n"));
        foreach (var n in bodyB.Nodes().ToList())
        {
            n.Remove();
            bodyA.Add(n);
        }
        TrimTrailingWhitespace(bodyA);
        bodyA.Add(new XText("\n"));
        int blocksBefore = chapter.Blocks.Count;
        Put(chapter.Path, Serialize(prolog, docA));

        // links: next.xhtml#x → this.xhtml#x (renamed ids applied), next.xhtml → this.xhtml#anchor
        RewriteAll((t, f) => t != next.Path ? null : (chapter.Path, f == null ? anchor : idMap.GetValueOrDefault(f, f)), except: next.Path);

        var opf = Opf();
        opf.RemoveItem(next.Path);
        opf.TouchModified();
        opf.Save();
        book.Package.Remove(next.Path);
        return new StructureChange("Unir capítulos", new(), [next.Path], [new BlockMove(next.Path, 0, chapter.Path, blocksBefore)], chapter.Path);
    });

    /// <summary>
    /// Rewrites links in every file, reading the package (during an operation the Chapter objects are stale:
    /// a Book view without chapters reads all text from the package).
    /// </summary>
    private void RewriteAll(Func<string, string?, (string, string?)?> map, string? except = null)
    {
        var view = new Book { FilePath = book.FilePath, Package = book.Package, Opf = book.Opf };
        var links = LinkIndex.Build(view).Where(l => l.Source != except).ToList();
        ApplyRewrites(LinkIndex.Rewrite(view, links, (t, f) => map(t, f)));
    }

    private static void TrimTrailingWhitespace(XElement e)
    {
        while (e.LastNode is XText t && t.Value.Trim().Length == 0) t.Remove();
    }

    private static string Serialize(string prolog, XDocument doc)
    {
        var sb = new StringBuilder(prolog);
        XmlFragmentWriter.Write(sb, doc.Root!);
        sb.Append('\n');
        return sb.ToString();
    }

    // ================================================================== other files

    /// <summary>Adds a file (image, font, stylesheet…) in the folder where the book keeps that kind of file.</summary>
    public StructureChange AddFile(string fileName, byte[] data) => Run(() =>
    {
        var path = EpubPath.Unique(FolderFor(fileName), SafeName(fileName), book.Package.Exists);
        book.Package.Put(path, data);
        var opf = Opf();
        opf.AddItem(path, MediaTypeFor(path));
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple("Añadir archivo", path);
    });

    private static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return EpubPath.Slug(Path.GetFileNameWithoutExtension(name)) + ext;
    }

    private string FolderFor(string fileName)
    {
        var type = MediaTypeFor(fileName);
        string Kind(string mt) => mt.StartsWith("image/") ? "image" : mt.StartsWith("font/") ? "font" : mt;
        var same = book.Opf.Manifest.Where(m => Kind(m.MediaType) == Kind(type)).Select(m => EpubPath.DirectoryOf(m.Path))
            .GroupBy(d => d).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (same != null) return same;
        var root = EpubPath.DirectoryOf(book.Opf.Path);
        return root + (Kind(type) switch { "image" => "Images/", "font" => "Fonts/", "text/css" => "Styles/", _ => "Misc/" });
    }

    /// <summary>Replaces a file's content. A different extension renames it (links follow).</summary>
    public StructureChange ReplaceFile(string path, string newFileName, byte[] data)
    {
        var ext = Path.GetExtension(newFileName).ToLowerInvariant();
        if (ext.Length > 0 && ext != EpubPath.Extension(path) && !(ext is ".jpeg" && EpubPath.Extension(path) == ".jpg"))
        {
            var renamed = Rename(path, Path.GetFileNameWithoutExtension(EpubPath.FileName(path)) + ext);
            var target = renamed.Focus!;
            var change = Run(() =>
            {
                book.Package.Put(target, data);
                return StructureChange.Simple("Reemplazar archivo", target);
            });
            return change with { Renamed = renamed.Renamed };
        }
        return Run(() =>
        {
            book.Package.Put(path, data);
            return StructureChange.Simple("Reemplazar archivo", path);
        });
    }

    /// <summary>Adds files that are in the zip but not in the manifest.</summary>
    public StructureChange AddToManifest(IReadOnlyCollection<string> paths) => Run(() =>
    {
        var opf = Opf();
        foreach (var p in paths) opf.AddItem(p, MediaTypeFor(p));
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple("Añadir al manifiesto");
    });

    /// <summary>Removes manifest items (and spine entries) whose file does not exist.</summary>
    public StructureChange RemoveFromManifest(IReadOnlyCollection<string> paths) => Run(() =>
    {
        var opf = Opf();
        foreach (var p in paths) opf.RemoveItem(p);
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple("Quitar del manifiesto");
    });

    public StructureChange SetCover(string imagePath) => Run(() =>
    {
        var opf = Opf();
        opf.SetCover(imagePath);
        opf.TouchModified();
        opf.Save();
        return StructureChange.Simple("Cambiar portada");
    });

    public BookMetadata GetMetadata() => Opf().GetMetadata();

    public StructureChange SetMetadata(BookMetadata m) => Run(() =>
    {
        var opf = Opf();
        opf.SetMetadata(m);
        opf.Save();
        return StructureChange.Simple("Metadatos");
    });

    // ================================================================== table of contents

    /// <summary>Current TOC as editable rows.</summary>
    public List<TocDraft> TocDrafts() => book.Toc.Select(e => new TocDraft(e.Title, e.Path, e.Fragment, e.Level)).ToList();

    /// <summary>
    /// A TOC built from the book: one entry per titled chapter (continuation parts left out), plus its
    /// headings h2/h3 below it. Ids for headings are created when the TOC is applied.
    /// </summary>
    public List<TocDraft> GenerateToc()
    {
        var list = new List<TocDraft>();
        foreach (var c in book.Chapters.Where(c => c.Linear && !c.IsContinuation && !c.IsImageOnly && c.WordCount > 0))
        {
            var heads = c.Blocks.Where(b => b.Kind == BlockKind.Heading && !b.IsNote && b.WordCount > 0).ToList();
            list.Add(new TocDraft(c.Title, c.Path, null, 0));
            int top = heads.Count == 0 ? 0 : heads.Min(h => h.HeadingLevel);
            foreach (var h in heads)
            {
                if (h.Index <= 2 && TextNormalizer.Fold(h.Text.Trim()) == TextNormalizer.Fold(c.Title.Trim())) continue; // the chapter title itself
                int level = Math.Clamp(h.HeadingLevel - top, 1, 2);
                if (h.HeadingLevel > top + 1) continue; // deeper levels clutter e-reader TOCs
                var id = (string?)h.Element?.Attribute("id");
                list.Add(new TocDraft(Chapter.Shorten(h.Text, 80), c.Path, id, level, id == null ? h.Index : -1));
            }
        }
        return list;
    }

    /// <summary>Writes the TOC (nav and/or NCX), adding ids to headings that need one.</summary>
    public StructureChange SetToc(IReadOnlyList<TocDraft> drafts) => Run(() =>
    {
        var entries = new List<TocEntry>();
        foreach (var group in drafts.Where(d => d.BlockIndex >= 0 && d.Fragment == null).GroupBy(d => d.Path))
        {
            var text = TextOf(group.Key);
            var doc = XmlLoader.Parse(text);
            var blocks = TextExtractor.Extract(doc, group.Key).Blocks;
            var ranges = XmlSourceScanner.Scan(text);
            var rangeOf = doc.Root!.DescendantsAndSelf().Zip(ranges).ToDictionary(p => p.First, p => p.Second);
            var used = doc.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>().ToHashSet();
            var sb = new StringBuilder(text);
            foreach (var d in group.OrderByDescending(d => d.BlockIndex))
            {
                if (d.BlockIndex >= blocks.Count || blocks[d.BlockIndex].Element is not { } el || blocks[d.BlockIndex].IsAnonymous) continue;
                int n = 1;
                string id;
                do id = $"kaku-toc-{n++}"; while (used.Contains(id));
                used.Add(id);
                el.SetAttributeValue("id", id);
                var r = rangeOf[el];
                var tag = r.SelfClosing ? XmlFragmentWriter.Outer(el) : XmlFragmentWriter.StartTag(el);
                sb.Remove(r.OuterStart, (r.SelfClosing ? r.OuterEnd : r.ContentStart) - r.OuterStart).Insert(r.OuterStart, tag);
                drafts = drafts.Select(x => ReferenceEquals(x, d) ? x with { Fragment = id } : x).ToList();
            }
            Put(group.Key, sb.ToString());
        }
        foreach (var d in drafts) entries.Add(new TocEntry(d.Title.Trim(), d.Path, d.Fragment, d.Level));
        TocWriter.Write(book, entries);
        return StructureChange.Simple("Índice");
    });
}
