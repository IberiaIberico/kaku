using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Structure;

/// <summary>One reference from a file to another file (or a fragment in it).</summary>
public sealed record LinkRef(
    string Source,
    int Line,
    string Attribute,
    string Raw,
    string Target,
    string? Fragment,
    /// <summary>Offset of the value in the source text (for rewriting).</summary>
    int ValueStart,
    int ValueLength);

/// <summary>
/// All references between the files of a book: href/src/xlink:href in XHTML, SVG, NCX and nav, url()
/// and @import in CSS. The OPF manifest itself is not included (it is edited through <see cref="OpfEditor"/>).
/// </summary>
public static partial class LinkIndex
{
    private static readonly HashSet<string> LinkAttributes = new(StringComparer.Ordinal) { "href", "src", "poster", "data" };

    [GeneratedRegex(@"url\(\s*(['""]?)([^'"")]+)\1\s*\)|@import\s+(['""])([^'""]+)\3", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    public static bool IsMarkup(string path) => EpubPath.Extension(path) is ".xhtml" or ".html" or ".htm" or ".xml" or ".ncx" or ".svg" or ".opf";

    public static List<LinkRef> Build(Book book, string? onlySource = null)
    {
        var result = new List<LinkRef>();
        foreach (var e in book.Package.Entries)
        {
            if (onlySource != null && e.Path != onlySource) continue;
            if (e.Path == book.Opf.Path || e.Path.StartsWith("META-INF/", StringComparison.Ordinal)) continue;
            var text = book.ChapterByPath(e.Path)?.SourceText ?? (IsMarkup(e.Path) || EpubPath.Extension(e.Path) == ".css" ? XmlLoader.DecodeText(e.Data) : null);
            if (text == null) continue;
            if (EpubPath.Extension(e.Path) == ".css") ScanCss(e.Path, text, result);
            else ScanMarkup(e.Path, text, result);
        }
        return result;
    }

    private static void ScanCss(string path, string css, List<LinkRef> result)
    {
        foreach (Match m in CssUrlRegex().Matches(css))
        {
            var g = m.Groups[2].Success ? m.Groups[2] : m.Groups[4];
            var raw = g.Value.Trim();
            if (raw.Length == 0 || EpubPath.IsExternal(raw) || raw.StartsWith('#')) continue;
            var (_, frag) = EpubPath.SplitHref(raw);
            result.Add(new LinkRef(path, LineAt(css, g.Index), "url", raw, EpubPath.Resolve(path, raw), frag, g.Index, g.Length));
        }
    }

    private static void ScanMarkup(string path, string text, List<LinkRef> result)
    {
        XDocument doc;
        try
        {
            doc = XmlLoader.Parse(text);
        }
        catch (XmlException)
        {
            return;
        }
        var ranges = XmlSourceScanner.Scan(text);
        int k = 0;
        foreach (var el in doc.Root!.DescendantsAndSelf())
        {
            if (k >= ranges.Count) break;
            var range = ranges[k++];
            foreach (var a in el.Attributes())
            {
                if (a.IsNamespaceDeclaration || !LinkAttributes.Contains(a.Name.LocalName)) continue;
                var raw = a.Value.Trim();
                if (raw.Length == 0 || EpubPath.IsExternal(raw)) continue;
                var (pathPart, frag) = EpubPath.SplitHref(raw);
                var target = pathPart.Length == 0 ? path : EpubPath.Resolve(path, raw);
                var (vs, vl) = FindAttributeValue(text, range.OuterStart, range.ContentStart, a);
                result.Add(new LinkRef(path, LineAt(text, range.OuterStart), a.Name.LocalName, raw, target, frag, vs, vl));
            }
            // inline style="background: url(...)"
            if ((string?)el.Attribute("style") is { } style && style.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                var (vs, _) = FindAttributeValue(text, range.OuterStart, range.ContentStart, el.Attribute("style")!);
                foreach (Match m in CssUrlRegex().Matches(text.Substring(vs, Math.Min(style.Length + 20, text.Length - vs))))
                {
                    var g = m.Groups[2].Success ? m.Groups[2] : m.Groups[4];
                    if (EpubPath.IsExternal(g.Value)) continue;
                    result.Add(new LinkRef(path, LineAt(text, vs), "style", g.Value, EpubPath.Resolve(path, g.Value), EpubPath.SplitHref(g.Value).Fragment, vs + g.Index, g.Length));
                }
            }
        }
    }

    /// <summary>Locates the value of an attribute inside a start tag (the raw, possibly escaped text).</summary>
    private static (int Start, int Length) FindAttributeValue(string text, int tagStart, int tagEnd, XAttribute a)
    {
        var tag = text.AsSpan(tagStart, tagEnd - tagStart);
        var local = a.Name.LocalName;
        int i = 0;
        while (i < tag.Length)
        {
            int at = tag[i..].IndexOf(local.AsSpan(), StringComparison.Ordinal);
            if (at < 0) break;
            at += i;
            bool startOk = at > 0 && (char.IsWhiteSpace(tag[at - 1]) || tag[at - 1] == ':');
            int j = at + local.Length;
            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (startOk && j < tag.Length && tag[j] == '=')
            {
                j++;
                while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
                if (j < tag.Length && tag[j] is '"' or '\'')
                {
                    char q = tag[j];
                    int end = tag[(j + 1)..].IndexOf(q);
                    if (end >= 0) return (tagStart + j + 1, end);
                }
            }
            i = at + local.Length;
        }
        return (tagStart, 0);
    }

    private static int LineAt(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// Rewrites references in place. <paramref name="map"/> receives (target path, fragment) and returns the
    /// new (path, fragment), or null to leave the reference alone. Values are rewritten relative to their
    /// own file. Returns the new text of every changed file.
    /// </summary>
    public static Dictionary<string, string> Rewrite(Book book, IEnumerable<LinkRef> links,
        Func<string, string?, (string Path, string? Fragment)?> map, IReadOnlyDictionary<string, string>? renamedSources = null)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in links.GroupBy(l => l.Source))
        {
            var source = group.Key;
            var text = book.ChapterByPath(source)?.SourceText ?? XmlLoader.DecodeText(book.Package.Read(source)!);
            // the file itself may be moving: links are written relative to its new location
            var newSource = renamedSources != null && renamedSources.TryGetValue(source, out var ns) ? ns : source;
            var sb = new StringBuilder(text);
            bool any = false;
            foreach (var l in group.Where(l => l.ValueLength > 0).OrderByDescending(l => l.ValueStart))
            {
                var mapped = map(l.Target, l.Fragment);
                if (mapped is not { } m)
                {
                    if (newSource == source) continue;
                    m = (l.Target, l.Fragment); // same target, but relative to the new location
                }
                var raw = m.Path == newSource && l.Raw.StartsWith('#') ? "#" + Uri.EscapeDataString(m.Fragment ?? "") : EpubPath.Relative(newSource, m.Path, m.Fragment);
                if (l.Attribute is not ("url" or "style")) raw = raw.Replace("&", "&amp;").Replace("\"", "&quot;");
                if (raw == text.Substring(l.ValueStart, l.ValueLength)) continue;
                sb.Remove(l.ValueStart, l.ValueLength).Insert(l.ValueStart, raw);
                any = true;
            }
            if (any) changed[source] = sb.ToString();
        }
        return changed;
    }
}
