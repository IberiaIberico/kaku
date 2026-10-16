using System.Text;
using System.Xml.Linq;

namespace Kaku.Core.Epub;

/// <summary>
/// Serializes nodes of a parsed document back to XHTML text <em>in context</em>: prefixes come from the
/// declarations in scope and namespace declarations are written only where they were in the original,
/// so the output can be spliced into the source without adding <c>xmlns</c> noise.
/// </summary>
public static class XmlFragmentWriter
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    public static string Inner(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var n in e.Nodes()) Write(sb, n);
        return sb.ToString();
    }

    public static string Outer(XNode n)
    {
        var sb = new StringBuilder();
        Write(sb, n);
        return sb.ToString();
    }

    public static string StartTag(XElement e)
    {
        var sb = new StringBuilder();
        WriteStartTag(sb, e);
        sb.Append('>');
        return sb.ToString();
    }

    public static string EndTag(XElement e) => $"</{QualifiedName(e)}>";

    public static void Write(StringBuilder sb, XNode node)
    {
        switch (node)
        {
            case XCData cdata:
                sb.Append("<![CDATA[").Append(cdata.Value).Append("]]>");
                break;
            case XText t:
                EscapeText(sb, t.Value);
                break;
            case XComment c:
                sb.Append("<!--").Append(c.Value).Append("-->");
                break;
            case XProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target).Append(' ').Append(pi.Data).Append("?>");
                break;
            case XElement e:
                WriteStartTag(sb, e);
                if (!e.Nodes().Any() && VoidElements.Contains(e.Name.LocalName))
                {
                    sb.Append("/>");
                    break;
                }
                sb.Append('>');
                foreach (var child in e.Nodes()) Write(sb, child);
                sb.Append("</").Append(QualifiedName(e)).Append('>');
                break;
        }
    }

    private static void WriteStartTag(StringBuilder sb, XElement e)
    {
        sb.Append('<').Append(QualifiedName(e));
        foreach (var a in e.Attributes())
        {
            sb.Append(' ');
            if (a.IsNamespaceDeclaration)
                sb.Append(a.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + a.Name.LocalName);
            else
                sb.Append(AttributeName(e, a));
            sb.Append("=\"");
            EscapeAttribute(sb, a.Value);
            sb.Append('"');
        }
    }

    private static string QualifiedName(XElement e)
    {
        var ns = e.Name.Namespace;
        if (ns == XNamespace.None) return e.Name.LocalName;
        var prefix = e.GetPrefixOfNamespace(ns);
        // null/empty prefix: it is the default namespace in scope
        return string.IsNullOrEmpty(prefix) || e.GetDefaultNamespace() == ns ? e.Name.LocalName : prefix + ":" + e.Name.LocalName;
    }

    private static string AttributeName(XElement owner, XAttribute a)
    {
        var ns = a.Name.Namespace;
        if (ns == XNamespace.None) return a.Name.LocalName;
        if (ns == XNamespace.Xml) return "xml:" + a.Name.LocalName;
        var prefix = owner.GetPrefixOfNamespace(ns);
        return string.IsNullOrEmpty(prefix) ? a.Name.LocalName : prefix + ":" + a.Name.LocalName;
    }

    private static void EscapeText(StringBuilder sb, string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static void EscapeAttribute(StringBuilder sb, string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\n': sb.Append("&#10;"); break;
                case '\r': sb.Append("&#13;"); break;
                case '\t': sb.Append("&#9;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
