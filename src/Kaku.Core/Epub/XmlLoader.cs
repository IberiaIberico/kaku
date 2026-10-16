using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kaku.Core.Epub;

/// <summary>Tolerant XML/XHTML loading: BOM-aware decoding, DTDs ignored, HTML named entities (&amp;nbsp; …) accepted.</summary>
public static partial class XmlLoader
{
    public const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    public const string OpsNs = "http://www.idpf.org/2007/ops";

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex EntityRegex();

    public static string DecodeText(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var sr = new StreamReader(ms, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>Replaces HTML named entities that XML does not know with numeric references (same line numbers).</summary>
    public static string FixEntities(string text)
    {
        if (!text.Contains('&')) return text;
        return EntityRegex().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (name is "amp" or "lt" or "gt" or "quot" or "apos") return m.Value;
            var decoded = WebUtility.HtmlDecode(m.Value);
            if (decoded == m.Value) return "&amp;" + name + ";"; // unknown entity: keep it visible as text
            var sb = new StringBuilder();
            foreach (var r in decoded.EnumerateRunes()) sb.Append("&#x").Append(r.Value.ToString("X")).Append(';');
            return sb.ToString();
        });
    }

    public static XDocument Parse(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
        };
        using var sr = new StringReader(FixEntities(text));
        using var xr = XmlReader.Create(sr, settings);
        return XDocument.Load(xr, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
    }

    public static XDocument Parse(byte[] data) => Parse(DecodeText(data));

    /// <summary>Value of an attribute matched by local name, in any namespace (e.g. epub:type, xlink:href).</summary>
    public static string? AttrLocal(XElement e, string localName)
    {
        foreach (var a in e.Attributes())
            if (a.Name.LocalName == localName) return a.Value;
        return null;
    }

    public static string Local(XElement e) => e.Name.LocalName.ToLowerInvariant();
}
