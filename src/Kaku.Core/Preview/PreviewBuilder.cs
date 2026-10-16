using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Preview;

/// <summary>
/// Produces the XHTML served to the preview: the chapter as-is, plus <c>data-kaku-b</c> attributes on
/// every block (so preview and editor can sync) and Kaku's preview script/stylesheet.
/// </summary>
public static class PreviewBuilder
{
    public const string BlockAttribute = "data-kaku-b";
    public const string ScriptPath = "__kaku/preview.js";
    public const string StylePath = "__kaku/preview.css";

    public static byte[] Build(Chapter chapter, string sourceText)
    {
        XDocument doc;
        try
        {
            doc = XmlLoader.Parse(sourceText);
        }
        catch (XmlException)
        {
            return Encoding.UTF8.GetBytes(sourceText);
        }

        // Same deterministic walk as the editor's text model, so block indexes match.
        var blocks = TextExtractor.Extract(doc, chapter.Path).Blocks;
        foreach (var b in blocks)
        {
            if (b.Element != null && b.Element.Attribute(BlockAttribute) == null)
                b.Element.SetAttributeValue(BlockAttribute, b.Index);
        }

        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var head = root.Elements().FirstOrDefault(e => e.Name.LocalName == "head");
        if (head == null)
        {
            head = new XElement(ns + "head");
            root.AddFirst(head);
        }
        head.Add(new XElement(ns + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", "/" + StylePath)));
        head.Add(new XElement(ns + "script", new XAttribute("type", "text/javascript"), new XAttribute("src", "/" + ScriptPath), ""));

        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            doc.Save(w);
        }
        return ms.ToArray();
    }
}
