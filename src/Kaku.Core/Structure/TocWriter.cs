using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Structure;

/// <summary>
/// Writes the table of contents into the nav document (EPUB 3) and the NCX (EPUB 2), whichever exist.
/// Only the list itself is replaced; headings, other navs (landmarks, page-list) and head stay as they are.
/// A book with neither gets a new NCX.
/// </summary>
public static class TocWriter
{
    private static readonly XNamespace NcxNs = "http://www.daisy.org/z3986/2005/ncx/";

    public static void Write(Book book, IReadOnlyList<TocEntry> entries)
    {
        var levels = Normalize(entries);
        if (book.Opf.NavPath is { } nav && book.Package.Exists(nav)) WriteNav(book, nav, levels);
        var ncx = book.Opf.NcxPath;
        if (ncx != null && book.Package.Exists(ncx)) WriteNcx(book, ncx, levels);
        else if (book.Opf.NavPath == null) CreateNcx(book, levels);
    }

    /// <summary>Levels must start at 0 and never jump by more than one.</summary>
    private static List<TocEntry> Normalize(IReadOnlyList<TocEntry> entries)
    {
        var list = new List<TocEntry>();
        int prev = -1;
        foreach (var e in entries)
        {
            int level = Math.Clamp(e.Level, 0, prev + 1);
            list.Add(e with { Level = level });
            prev = level;
        }
        return list;
    }

    // ------------------------------------------------------------------ nav.xhtml

    private static void WriteNav(Book book, string navPath, List<TocEntry> entries)
    {
        var text = XmlLoader.DecodeText(book.Package.Read(navPath)!);
        var doc = XmlLoader.Parse(text);
        var ranges = XmlSourceScanner.Scan(text);
        var map = doc.Root!.DescendantsAndSelf().Zip(ranges).ToDictionary(p => p.First, p => p.Second);
        var navs = doc.Descendants().Where(e => e.Name.LocalName == "nav").ToList();
        var toc = navs.FirstOrDefault(n => (XmlLoader.AttrLocal(n, "type") ?? "").Split(' ').Contains("toc")) ?? navs.FirstOrDefault();
        if (toc == null) return;
        var ns = toc.Name.Namespace;
        var oldOl = toc.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");

        // build the new list attached to the document, so prefixes serialize correctly
        string indent = IndentBefore(text, oldOl != null ? map[oldOl].OuterStart : map[toc].ContentEnd);
        var ol = BuildOl(ns, navPath, entries, 0, entries.Count, indent);
        toc.Add(ol);
        var html = XmlFragmentWriter.Outer(ol);
        ol.Remove();

        string updated = oldOl != null
            ? text[..map[oldOl].OuterStart] + html + text[map[oldOl].OuterEnd..]
            : text[..map[toc].ContentEnd] + html + "\n" + text[map[toc].ContentEnd..];
        book.Package.Put(navPath, Encoding.UTF8.GetBytes(updated));
    }

    private static XElement BuildOl(XNamespace ns, string navPath, List<TocEntry> entries, int from, int to, string indent)
    {
        var ol = new XElement(ns + "ol");
        int i = from;
        while (i < to)
        {
            int level = entries[i].Level;
            int end = i + 1;
            while (end < to && entries[end].Level > level) end++;
            var e = entries[i];
            var li = new XElement(ns + "li", new XElement(ns + "a", new XAttribute("href", EpubPath.Relative(navPath, e.Path, e.Fragment)), e.Title));
            if (end > i + 1)
            {
                var childIndent = indent + "    ";
                li.Add(new XText("\n" + childIndent), BuildOl(ns, navPath, entries, i + 1, end, childIndent), new XText("\n" + indent + "  "));
            }
            ol.Add(new XText("\n" + indent + "  "), li);
            i = end;
        }
        ol.Add(new XText("\n" + indent));
        return ol;
    }

    private static string IndentBefore(string text, int offset)
    {
        int i = offset;
        while (i > 0 && text[i - 1] is ' ' or '\t') i--;
        return text[i..offset];
    }

    // ------------------------------------------------------------------ toc.ncx

    private static void WriteNcx(Book book, string ncxPath, List<TocEntry> entries)
    {
        var text = XmlLoader.DecodeText(book.Package.Read(ncxPath)!);
        var doc = XmlLoader.Parse(text);
        var ranges = XmlSourceScanner.Scan(text);
        var map = doc.Root!.DescendantsAndSelf().Zip(ranges).ToDictionary(p => p.First, p => p.Second);
        var navMap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
        var ns = doc.Root.Name.Namespace;
        if (navMap == null) return;

        int order = 0;
        var children = BuildNavPoints(ns, ncxPath, entries, 0, entries.Count, "  ", ref order);
        var holder = new XElement(navMap.Name);
        navMap.AddAfterSelf(holder);
        var sb = new StringBuilder();
        foreach (var c in children)
        {
            holder.Add(c);
            sb.Append("\n  ");
            XmlFragmentWriter.Write(sb, c);
        }
        sb.Append('\n');
        holder.Remove();

        var r = map[navMap];
        string updated = r.SelfClosing
            ? text[..r.OuterStart] + XmlFragmentWriter.StartTag(navMap) + sb + "</navMap>" + text[r.OuterEnd..]
            : text[..r.ContentStart] + sb + text[r.ContentEnd..];
        // dtb:depth must match the deepest level
        int depth = entries.Count == 0 ? 1 : entries.Max(e => e.Level) + 1;
        updated = System.Text.RegularExpressions.Regex.Replace(updated, @"(<meta\s+[^>]*name=""dtb:depth""\s+content="")(\d+)("")", m => m.Groups[1].Value + depth + m.Groups[3].Value);
        book.Package.Put(ncxPath, Encoding.UTF8.GetBytes(updated));
    }

    private static List<XElement> BuildNavPoints(XNamespace ns, string ncxPath, List<TocEntry> entries, int from, int to, string indent, ref int order)
    {
        var list = new List<XElement>();
        int i = from;
        while (i < to)
        {
            int level = entries[i].Level;
            int end = i + 1;
            while (end < to && entries[end].Level > level) end++;
            var e = entries[i];
            order++;
            var np = new XElement(ns + "navPoint", new XAttribute("id", $"navPoint-{order}"), new XAttribute("playOrder", order),
                new XText("\n" + indent + "  "), new XElement(ns + "navLabel", new XElement(ns + "text", e.Title)),
                new XText("\n" + indent + "  "), new XElement(ns + "content", new XAttribute("src", EpubPath.Relative(ncxPath, e.Path, e.Fragment))));
            foreach (var child in BuildNavPoints(ns, ncxPath, entries, i + 1, end, indent + "  ", ref order))
                np.Add(new XText("\n" + indent + "  "), child);
            np.Add(new XText("\n" + indent));
            list.Add(np);
            i = end;
        }
        return list;
    }

    private static void CreateNcx(Book book, List<TocEntry> entries)
    {
        var dir = EpubPath.DirectoryOf(book.Opf.Path);
        var ncxPath = EpubPath.Unique(dir, "toc.ncx", book.Package.Exists);
        var skeleton = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
            <head>
              <meta name="dtb:uid" content="kaku"/>
              <meta name="dtb:depth" content="1"/>
            </head>
            <docTitle><text>{System.Security.SecurityElement.Escape(book.Title)}</text></docTitle>
            <navMap></navMap>
            </ncx>

            """;
        book.Package.Put(ncxPath, Encoding.UTF8.GetBytes(skeleton));
        var opf = new OpfEditor(book.Package, book.Opf.Path);
        opf.AddItem(ncxPath, "application/x-dtbncx+xml");
        opf.SetSpineToc(ncxPath);
        opf.Save();
        WriteNcx(book, ncxPath, entries);
    }
}
