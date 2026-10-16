using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Notes;

/// <summary>
/// The text format of the note editor: plain text where **this** is bold and *this* is italic.
/// Converts to and from the XHTML stored in the book (other markup is kept as plain text).
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRe();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex ItalicRe();

    public static string ToXhtml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var e = SecurityElement.Escape(string.Join(" ", lines)) ?? "";
        e = BoldRe().Replace(e, "<b>$1</b>");
        return ItalicRe().Replace(e, "<i>$1</i>");
    }

    public static string FromXhtml(string xhtml)
    {
        try
        {
            var root = XElement.Parse("<r>" + XmlLoader.FixEntities(xhtml) + "</r>", LoadOptions.PreserveWhitespace);
            var sb = new StringBuilder();
            foreach (var n in root.Nodes()) Write(sb, n);
            return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return xhtml;
        }
    }

    private static void Write(StringBuilder sb, XNode n)
    {
        switch (n)
        {
            case XText t:
                sb.Append(t.Value);
                break;
            case XElement e:
                var name = e.Name.LocalName;
                var mark = name is "b" or "strong" ? "**" : name is "i" or "em" or "cite" ? "*" : "";
                sb.Append(mark);
                foreach (var c in e.Nodes()) Write(sb, c);
                sb.Append(mark);
                break;
        }
    }

    /// <summary>Plain text of a note (for lists and tooltips).</summary>
    public static string PlainText(string xhtml) => FromXhtml(xhtml).Replace("**", "").Replace("*", "");
}
