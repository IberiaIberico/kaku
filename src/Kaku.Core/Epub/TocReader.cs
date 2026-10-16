using System.Xml.Linq;

namespace Kaku.Core.Epub;

/// <summary>One entry of the table of contents, flattened with its nesting level (0 = top).</summary>
public sealed record TocEntry(string Title, string Path, string? Fragment, int Level);

/// <summary>Reads the TOC from the EPUB 3 nav document, falling back to the EPUB 2 NCX.</summary>
public static class TocReader
{
    public static List<TocEntry> Read(EpubPackage pkg, OpfDocument opf)
    {
        var result = new List<TocEntry>();
        try
        {
            if (opf.NavPath != null && pkg.Read(opf.NavPath) is { } nav) ReadNav(opf.NavPath, XmlLoader.Parse(nav), result);
            if (result.Count == 0 && opf.NcxPath != null && pkg.Read(opf.NcxPath) is { } ncx) ReadNcx(opf.NcxPath, XmlLoader.Parse(ncx), result);
        }
        catch (System.Xml.XmlException)
        {
            // a broken TOC must not prevent opening the book
        }
        return result;
    }

    public static void ReadNav(string navPath, XDocument doc, List<TocEntry> result)
    {
        var navs = doc.Descendants().Where(e => e.Name.LocalName == "nav").ToList();
        var toc = navs.FirstOrDefault(n => (XmlLoader.AttrLocal(n, "type") ?? "").Split(' ').Contains("toc")) ?? navs.FirstOrDefault();
        var ol = toc?.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
        if (ol != null) ReadOl(navPath, ol, 0, result);
    }

    private static void ReadOl(string navPath, XElement ol, int level, List<TocEntry> result)
    {
        foreach (var li in ol.Elements().Where(e => e.Name.LocalName == "li"))
        {
            var label = li.Elements().FirstOrDefault(e => e.Name.LocalName is "a" or "span");
            if (label != null)
            {
                var href = (string?)label.Attribute("href");
                var title = Collapse(label.Value);
                if (href != null && !EpubPath.IsExternal(href))
                    result.Add(new TocEntry(title, EpubPath.Resolve(navPath, href), EpubPath.SplitHref(href).Fragment, level));
            }
            var sub = li.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
            if (sub != null) ReadOl(navPath, sub, level + 1, result);
        }
    }

    public static void ReadNcx(string ncxPath, XDocument doc, List<TocEntry> result)
    {
        var navMap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
        if (navMap != null) ReadNavPoints(ncxPath, navMap, 0, result);
    }

    private static void ReadNavPoints(string ncxPath, XElement parent, int level, List<TocEntry> result)
    {
        foreach (var np in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
        {
            var text = np.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value ?? "";
            var src = (string?)np.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
            if (src != null && !EpubPath.IsExternal(src))
                result.Add(new TocEntry(Collapse(text), EpubPath.Resolve(ncxPath, src), EpubPath.SplitHref(src).Fragment, level));
            ReadNavPoints(ncxPath, np, level + 1, result);
        }
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
