using System.Text;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Structure;

/// <summary>Book metadata as shown in the metadata editor.</summary>
public sealed record BookMetadata(
    string Title,
    List<string> Creators,
    string Language,
    string Publisher,
    string Description,
    string Series,
    string SeriesIndex);

/// <summary>
/// Edits the package document (.opf) as XML: manifest, spine, cover and metadata. Unknown metadata and
/// vendor extensions are kept. <see cref="Save"/> writes the document back into the package.
/// </summary>
public sealed class OpfEditor
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private readonly EpubPackage _pkg;
    private readonly string _prolog;

    public OpfEditor(EpubPackage pkg, string opfPath)
    {
        _pkg = pkg;
        Path = opfPath;
        var text = XmlLoader.DecodeText(pkg.Read(opfPath)!);
        Doc = XmlLoader.Parse(text);
        var rootStart = XmlSourceScanner.Scan(text).FirstOrDefault().OuterStart;
        _prolog = text[..rootStart];
        Ns = Doc.Root!.Name.Namespace;
    }

    public string Path { get; }
    public XDocument Doc { get; }
    private XNamespace Ns { get; }
    private XElement Root => Doc.Root!;
    private XElement Manifest => Child(Root, "manifest") ?? Add(Root, "manifest");
    private XElement Spine => Child(Root, "spine") ?? Add(Root, "spine");
    private XElement Metadata => Child(Root, "metadata") ?? Add(Root, "metadata");
    public bool IsEpub3 => ((string?)Root.Attribute("version") ?? "").StartsWith('3');

    private static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(x => x.Name.LocalName == local);

    private XElement Add(XElement parent, string local)
    {
        var e = new XElement(Ns + local);
        parent.Add(e);
        return e;
    }

    private IEnumerable<XElement> Items => Manifest.Elements().Where(e => e.Name.LocalName == "item");
    private IEnumerable<XElement> ItemRefs => Spine.Elements().Where(e => e.Name.LocalName == "itemref");

    private string Href(string zipPath) => EpubPath.Relative(Path, zipPath);

    public XElement? ItemFor(string zipPath) =>
        Items.FirstOrDefault(i => (string?)i.Attribute("href") is { } h && EpubPath.Resolve(Path, h) == zipPath);

    public string? IdFor(string zipPath) => (string?)ItemFor(zipPath)?.Attribute("id");

    private string NewId(string zipPath)
    {
        var used = Root.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i != null).ToHashSet();
        var baseId = "kaku-" + EpubPath.Slug(System.IO.Path.GetFileNameWithoutExtension(EpubPath.FileName(zipPath)));
        var id = baseId;
        for (int n = 2; used.Contains(id); n++) id = $"{baseId}-{n}";
        return id;
    }

    // ------------------------------------------------------------------ manifest / spine

    public string AddItem(string zipPath, string mediaType, string? properties = null)
    {
        if (IdFor(zipPath) is { } existing) return existing;
        var id = NewId(zipPath);
        var item = new XElement(Ns + "item", new XAttribute("id", id), new XAttribute("href", Href(zipPath)), new XAttribute("media-type", mediaType));
        if (!string.IsNullOrEmpty(properties)) item.SetAttributeValue("properties", properties);
        AppendIndented(Manifest, item);
        return id;
    }

    /// <summary>Removes the manifest item, its spine references and cover markers.</summary>
    public void RemoveItem(string zipPath)
    {
        var item = ItemFor(zipPath);
        if (item == null) return;
        var id = (string?)item.Attribute("id");
        RemoveWithWhitespace(item);
        foreach (var r in ItemRefs.Where(r => (string?)r.Attribute("idref") == id).ToList()) RemoveWithWhitespace(r);
        foreach (var m in Metadata.Elements().Where(m => m.Name.LocalName == "meta" && (string?)m.Attribute("name") == "cover" && (string?)m.Attribute("content") == id).ToList())
            RemoveWithWhitespace(m);
        foreach (var g in Root.Descendants().Where(e => e.Name.LocalName == "reference" && (string?)e.Attribute("href") is { } h && EpubPath.Resolve(Path, h) == zipPath).ToList())
            RemoveWithWhitespace(g);
    }

    public void RenameItem(string from, string to)
    {
        if (ItemFor(from) is { } item) item.SetAttributeValue("href", Href(to));
        foreach (var g in Root.Descendants().Where(e => e.Name.LocalName == "reference" && (string?)e.Attribute("href") is { } h && EpubPath.Resolve(Path, h) == from))
        {
            var frag = EpubPath.SplitHref((string)g.Attribute("href")!).Fragment;
            g.SetAttributeValue("href", EpubPath.Relative(Path, to, frag));
        }
    }

    public void SetMediaType(string zipPath, string mediaType) => ItemFor(zipPath)?.SetAttributeValue("media-type", mediaType);

    /// <summary>Spine order as manifest paths.</summary>
    public List<string> SpinePaths()
    {
        var byId = Items.Where(i => i.Attribute("id") != null && i.Attribute("href") != null)
            .GroupBy(i => (string)i.Attribute("id")!).ToDictionary(g => g.Key, g => EpubPath.Resolve(Path, (string)g.First().Attribute("href")!));
        return ItemRefs.Select(r => byId.GetValueOrDefault((string?)r.Attribute("idref") ?? "")).OfType<string>().ToList();
    }

    /// <summary>Inserts a spine reference so the file becomes the <paramref name="index"/>-th reading-order document.</summary>
    public void InsertInSpine(string zipPath, int index)
    {
        var id = IdFor(zipPath) ?? throw new InvalidOperationException("No está en el manifiesto: " + zipPath);
        var refs = ItemRefs.ToList();
        var itemref = new XElement(Ns + "itemref", new XAttribute("idref", id));
        if (refs.Count == 0) AppendIndented(Spine, itemref);
        else if (index >= refs.Count) InsertAfter(refs[^1], itemref);
        else InsertBefore(refs[Math.Max(0, index)], itemref);
    }

    /// <summary>Moves one spine entry to a new position (0-based, in the order after removal).</summary>
    public void MoveInSpine(string zipPath, int newIndex)
    {
        var id = IdFor(zipPath);
        var r = ItemRefs.FirstOrDefault(x => (string?)x.Attribute("idref") == id);
        if (r == null) return;
        RemoveWithWhitespace(r);
        var refs = ItemRefs.ToList();
        var copy = new XElement(r);
        if (refs.Count == 0) AppendIndented(Spine, copy);
        else if (newIndex >= refs.Count) InsertAfter(refs[^1], copy);
        else InsertBefore(refs[Math.Max(0, newIndex)], copy);
    }

    public string? NcxPath =>
        (string?)Spine.Attribute("toc") is { } tocId && Items.FirstOrDefault(i => (string?)i.Attribute("id") == tocId) is { } ncx
            ? EpubPath.Resolve(Path, (string)ncx.Attribute("href")!) : null;

    public void SetSpineToc(string ncxPath) => Spine.SetAttributeValue("toc", IdFor(ncxPath));

    // ------------------------------------------------------------------ Kaku settings (&lt;meta name="kaku:…"&gt;)

    public string? GetMeta(string name) =>
        (string?)Metadata.Elements().FirstOrDefault(m => m.Name.LocalName == "meta" && (string?)m.Attribute("name") == name)?.Attribute("content");

    public void SetMeta(string name, string? value)
    {
        var m = Metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && (string?)x.Attribute("name") == name);
        if (value == null)
        {
            if (m != null) RemoveWithWhitespace(m);
            return;
        }
        if (m == null)
        {
            m = new XElement(Ns + "meta", new XAttribute("name", name));
            AppendIndented(Metadata, m);
        }
        m.SetAttributeValue("content", value);
    }

    // ------------------------------------------------------------------ cover

    public void SetCover(string imagePath)
    {
        foreach (var i in Items)
        {
            var props = ((string?)i.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "cover-image").ToList();
            i.SetAttributeValue("properties", props.Count == 0 ? null : string.Join(' ', props));
        }
        var item = ItemFor(imagePath);
        if (item == null) return;
        if (IsEpub3)
        {
            var props = ((string?)item.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Append("cover-image");
            item.SetAttributeValue("properties", string.Join(' ', props));
        }
        // EPUB 2 marker, read by most readers (Kobo included)
        var meta = Metadata.Elements().FirstOrDefault(m => m.Name.LocalName == "meta" && (string?)m.Attribute("name") == "cover");
        if (meta == null)
        {
            meta = new XElement(Ns + "meta", new XAttribute("name", "cover"));
            AppendIndented(Metadata, meta);
        }
        meta.SetAttributeValue("content", (string?)item.Attribute("id"));
    }

    // ------------------------------------------------------------------ metadata

    public BookMetadata GetMetadata()
    {
        string Dcv(string local) => Metadata.Elements().FirstOrDefault(e => e.Name == Dc + local)?.Value.Trim() ?? "";
        var creators = Metadata.Elements().Where(e => e.Name == Dc + "creator").Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
        string series = "", index = "";
        var coll = Metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == "belongs-to-collection");
        if (coll != null)
        {
            series = coll.Value.Trim();
            var cid = (string?)coll.Attribute("id");
            index = Metadata.Elements().FirstOrDefault(e => (string?)e.Attribute("refines") == "#" + cid && (string?)e.Attribute("property") == "group-position")?.Value.Trim() ?? "";
        }
        if (series.Length == 0)
        {
            series = (string?)Metadata.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == "calibre:series")?.Attribute("content") ?? "";
            index = (string?)Metadata.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == "calibre:series_index")?.Attribute("content") ?? "";
        }
        return new BookMetadata(Dcv("title"), creators, Dcv("language"), Dcv("publisher"), Dcv("description"), series, index);
    }

    public void SetMetadata(BookMetadata m)
    {
        SetDc("title", m.Title);
        SetDc("language", m.Language);
        SetDc("publisher", m.Publisher);
        SetDc("description", m.Description);

        // creators: update in place (keeps their refines: role, file-as), add or remove the rest
        var existing = Metadata.Elements().Where(e => e.Name == Dc + "creator").ToList();
        var names = m.Creators.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        for (int i = 0; i < Math.Max(existing.Count, names.Count); i++)
        {
            if (i < existing.Count && i < names.Count)
            {
                if (existing[i].Value != names[i])
                {
                    existing[i].Value = names[i];
                    RemoveRefines(existing[i], "file-as"); // the old sort name no longer matches
                }
            }
            else if (i < names.Count)
            {
                var e = new XElement(Dc + "creator", names[i]);
                if (existing.Count > 0) InsertAfter(existing[^1], e); else AppendIndented(Metadata, e);
                existing.Add(e);
            }
            else
            {
                RemoveRefines(existing[i], null);
                RemoveWithWhitespace(existing[i]);
            }
        }

        // series: EPUB 3 collection + calibre metas
        foreach (var e in Metadata.Elements().Where(e => (string?)e.Attribute("name") is "calibre:series" or "calibre:series_index").ToList()) RemoveWithWhitespace(e);
        var coll = Metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == "belongs-to-collection");
        if (coll != null)
        {
            RemoveRefines(coll, null);
            RemoveWithWhitespace(coll);
        }
        if (m.Series.Trim().Length > 0)
        {
            if (IsEpub3)
            {
                var c = new XElement(Ns + "meta", new XAttribute("property", "belongs-to-collection"), new XAttribute("id", "kaku-series"), m.Series.Trim());
                AppendIndented(Metadata, c);
                AppendIndented(Metadata, new XElement(Ns + "meta", new XAttribute("refines", "#kaku-series"), new XAttribute("property", "collection-type"), "series"));
                if (m.SeriesIndex.Trim().Length > 0)
                    AppendIndented(Metadata, new XElement(Ns + "meta", new XAttribute("refines", "#kaku-series"), new XAttribute("property", "group-position"), m.SeriesIndex.Trim()));
            }
            AppendIndented(Metadata, new XElement(Ns + "meta", new XAttribute("name", "calibre:series"), new XAttribute("content", m.Series.Trim())));
            if (m.SeriesIndex.Trim().Length > 0)
                AppendIndented(Metadata, new XElement(Ns + "meta", new XAttribute("name", "calibre:series_index"), new XAttribute("content", m.SeriesIndex.Trim())));
        }
        TouchModified();
    }

    private void SetDc(string local, string value)
    {
        var e = Metadata.Elements().FirstOrDefault(x => x.Name == Dc + local);
        if (value.Trim().Length == 0)
        {
            if (e != null && local is "publisher" or "description") RemoveWithWhitespace(e);
            return;
        }
        if (e == null)
        {
            e = new XElement(Dc + local);
            AppendIndented(Metadata, e);
        }
        if (e.Value != value.Trim()) e.Value = value.Trim();
    }

    private void RemoveRefines(XElement target, string? property)
    {
        var id = (string?)target.Attribute("id");
        if (id == null) return;
        foreach (var r in Metadata.Elements().Where(e => (string?)e.Attribute("refines") == "#" + id && (property == null || (string?)e.Attribute("property") == property)).ToList())
            RemoveWithWhitespace(r);
    }

    /// <summary>EPUB 3 requires dcterms:modified to change when the book changes.</summary>
    public void TouchModified()
    {
        if (!IsEpub3) return;
        var m = Metadata.Elements().FirstOrDefault(e => (string?)e.Attribute("property") == "dcterms:modified");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (m == null) AppendIndented(Metadata, new XElement(Ns + "meta", new XAttribute("property", "dcterms:modified"), now));
        else m.Value = now;
    }

    // ------------------------------------------------------------------ output

    public void Save()
    {
        var sb = new StringBuilder(_prolog);
        XmlFragmentWriter.Write(sb, Root);
        sb.Append('\n');
        _pkg.Put(Path, new UTF8Encoding(false).GetBytes(sb.ToString()));
    }

    // whitespace-aware tree edits, so the file stays readable

    private static string IndentOf(XElement e) =>
        e.PreviousNode is XText t && t.Value.LastIndexOf('\n') is var nl and >= 0 ? t.Value[(nl + 1)..] : "";

    private static void InsertAfter(XElement anchor, XElement e)
    {
        anchor.AddAfterSelf(new XText("\n" + IndentOf(anchor)), e);
    }

    private static void InsertBefore(XElement anchor, XElement e)
    {
        anchor.AddBeforeSelf(e, new XText("\n" + IndentOf(anchor)));
    }

    private static void AppendIndented(XElement parent, XElement e)
    {
        var last = parent.Elements().LastOrDefault();
        if (last != null) InsertAfter(last, e);
        else parent.Add(new XText("\n  "), e, new XText("\n"));
    }

    private static void RemoveWithWhitespace(XElement e)
    {
        if (e.PreviousNode is XText t && t.Value.Trim().Length == 0) t.Remove();
        e.Remove();
    }
}
