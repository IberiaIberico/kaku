using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;

namespace Kaku.Core.Text;

public sealed class ExtractionResult
{
    public List<ContentBlock> Blocks { get; } = new();
    /// <summary>Element id → index of the block that contains it (or the next block, for containers).</summary>
    public Dictionary<string, int> IdToBlock { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Splits an XHTML document into text blocks: the innermost block-level elements (p, h1–h6, li, div
/// without block children, …). Loose inline content inside containers becomes an anonymous block.
/// </summary>
public static class TextExtractor
{
    public const char LineBreakChar = '↵';

    private static readonly HashSet<string> BlockNames = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "dt", "dd", "td", "th", "caption", "blockquote", "div",
        "section", "article", "aside", "header", "footer", "figure", "figcaption", "pre", "nav", "ol", "ul", "dl",
        "table", "thead", "tbody", "tfoot", "tr", "main", "address", "hr", "body", "form", "fieldset", "legend",
        "center", "details", "summary", "hgroup",
    };

    private static readonly HashSet<string> SkipNames = new(StringComparer.Ordinal)
    {
        "head", "script", "style", "rt", "rp", "template", "noscript",
    };

    /// <summary>Leaf blocks kept even when empty (an empty paragraph is still a line you can type into).</summary>
    internal static readonly HashSet<string> KeepEmpty = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "dt", "dd", "blockquote", "pre", "figcaption", "caption", "td", "th",
    };

    private static readonly HashSet<string> NoteTypes = new(StringComparer.Ordinal)
    {
        "footnote", "endnote", "rearnote", "note", "footnotes", "endnotes", "rearnotes",
    };

    public static ExtractionResult Extract(XDocument doc, string? documentPath = null)
    {
        var result = new ExtractionResult();
        var root = doc.Root;
        if (root == null) return result;
        var body = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? root;
        new Walker(result, documentPath ?? "").WalkContainer(body, inNote: false);

        int words = 0;
        foreach (var b in result.Blocks)
        {
            b.WordStart = words;
            if (!b.IsNote) words += b.WordCount;
        }
        foreach (var key in result.IdToBlock.Keys.ToList())
            result.IdToBlock[key] = Math.Min(result.IdToBlock[key], Math.Max(0, result.Blocks.Count - 1));
        return result;
    }

    private static bool IsBlock(XElement e) => BlockNames.Contains(XmlLoader.Local(e));

    private static bool HasBlockDescendant(XElement e) => e.Descendants().Any(IsBlock);

    private static bool IsNoteElement(XElement e)
    {
        var type = XmlLoader.AttrLocal(e, "type");
        if (type != null && type.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(NoteTypes.Contains)) return true;
        var role = (string?)e.Attribute("role");
        return role is "doc-footnote" or "doc-endnote" or "doc-endnotes";
    }

    private static bool IsNoteRef(XElement e)
    {
        var type = XmlLoader.AttrLocal(e, "type");
        return type != null && type.Split(' ').Contains("noteref") || (string?)e.Attribute("role") == "doc-noteref";
    }

    private static int LineOf(XObject? o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

    // XmlReader reports the column of the element name; step back over '<'.
    private static int ColumnOf(XObject? o) => o is IXmlLineInfo li && li.HasLineInfo()
        ? Math.Max(1, li.LinePosition - (o is XElement ? 1 : 0)) : 0;

    private sealed class Walker(ExtractionResult result, string documentPath)
    {
        public void WalkContainer(XElement container, bool inNote)
        {
            inNote |= IsNoteElement(container);
            RecordId(container);
            var pending = new List<XNode>();
            foreach (var node in container.Nodes())
            {
                if (node is XElement ce)
                {
                    var name = XmlLoader.Local(ce);
                    if (SkipNames.Contains(name)) continue;
                    if (name == "hr") { Flush(container, pending, inNote); continue; }
                    if (IsBlock(ce))
                    {
                        Flush(container, pending, inNote);
                        if (HasBlockDescendant(ce)) WalkContainer(ce, inNote);
                        else EmitBlock(ce, ce.Nodes(), inNote || IsNoteElement(ce), ce);
                        continue;
                    }
                    if (HasBlockDescendant(ce))
                    {
                        // inline element wrapping blocks (e.g. <a><div>…</div></a>): treat as a container
                        Flush(container, pending, inNote);
                        WalkContainer(ce, inNote);
                        continue;
                    }
                }
                pending.Add(node);
            }
            Flush(container, pending, inNote);
        }

        private void Flush(XElement container, List<XNode> pending, bool inNote)
        {
            if (pending.Count == 0) return;
            EmitBlock(container, pending.ToList(), inNote, pending[0]);
            pending.Clear();
        }

        private void RecordId(XElement e)
        {
            var id = (string?)e.Attribute("id");
            if (!string.IsNullOrEmpty(id)) result.IdToBlock.TryAdd(id, result.Blocks.Count);
        }

        private void EmitBlock(XElement element, IEnumerable<XNode> nodes, bool inNote, XObject lineSource)
        {
            int index = result.Blocks.Count;
            if (ReferenceEquals(lineSource, element)) RecordId(element);
            var b = new InlineBuilder(this, index, preserveSpace: XmlLoader.Local(element) == "pre");
            foreach (var n in nodes) b.Walk(n);
            var (text, spans, map) = b.Finish();
            var name = XmlLoader.Local(element);
            if (text.Length == 0 && !(KeepEmpty.Contains(name) && ReferenceEquals(lineSource, element))) return;

            bool onlyPlaceholders = spans.Any(s => s.Kind == SpanKind.Image)
                && WordCounter.Count(WordCounter.Blank(text, spans.Where(s => s.IsPlaceholder))) == 0;
            var kind = name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]) && ReferenceEquals(lineSource, element)
                ? BlockKind.Heading
                : onlyPlaceholders ? BlockKind.Image : BlockKind.Paragraph;
            var wordText = WordCounter.Blank(text, spans.Where(s => s.IsPlaceholder || s.Kind == SpanKind.NoteRef)); // note numbers are not words
            result.Blocks.Add(new ContentBlock
            {
                Index = index,
                Tag = ReferenceEquals(lineSource, element) ? name : "#text",
                Kind = kind,
                HeadingLevel = kind == BlockKind.Heading ? name[1] - '0' : 0,
                Text = text,
                Spans = spans,
                Element = element,
                SourceLine = LineOf(lineSource) is > 0 and var l ? l : LineOf(element),
                SourceColumn = LineOf(lineSource) > 0 ? ColumnOf(lineSource) : ColumnOf(element),
                IsNote = inNote,
                WordText = wordText,
                WordCount = WordCounter.Count(wordText),
                Map = map,
            });
        }

        internal void RecordInlineId(XElement e, int blockIndex)
        {
            var id = (string?)e.Attribute("id");
            if (!string.IsNullOrEmpty(id)) result.IdToBlock.TryAdd(id, blockIndex);
        }

        internal string DocumentPath => documentPath;
    }

    /// <summary>Builds the visible text of one block, collapsing whitespace like a browser.</summary>
    private sealed class InlineBuilder(Walker walker, int blockIndex, bool preserveSpace)
    {
        private readonly StringBuilder _sb = new();
        private readonly List<InlineSpan> _spans = new();
        private readonly List<CharSource> _map = new();
        private bool _lastSpace = true;

        public void Walk(XNode node)
        {
            switch (node)
            {
                case XText t:
                    AppendText(t);
                    break;
                case XElement e:
                    WalkElement(e);
                    break;
            }
        }

        private void Append(char c, XText node, int i)
        {
            _sb.Append(c);
            _map.Add(new CharSource { Node = node, Start = i, End = i + 1 });
        }

        private void AppendText(XText t)
        {
            var s = t.Value;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    if (preserveSpace)
                    {
                        if (c == '\n')
                        {
                            _spans.Add(new InlineSpan(SpanKind.LineBreak, _sb.Length, 1));
                            Append(LineBreakChar, t, i);
                        }
                        else if (c != '\r') Append(' ', t, i);
                        continue;
                    }
                    if (!_lastSpace)
                    {
                        Append(' ', t, i);
                        _lastSpace = true;
                    }
                    else if (_map.Count > 0 && _sb[^1] == ' ' && _map[^1].Node != null)
                    {
                        // collapsed whitespace: belongs to the space already shown
                        var last = _map[^1];
                        if (ReferenceEquals(last.Node, t) && last.End == i) last.End = i + 1;
                        else (last.Extra ??= new()).Add((t, i, i + 1));
                    }
                }
                else
                {
                    Append(c, t, i);
                    _lastSpace = false;
                }
            }
        }

        private void AppendPlaceholder(SpanKind kind, string text, string? data, XNode element)
        {
            _spans.Add(new InlineSpan(kind, _sb.Length, text.Length, data));
            _sb.Append(text);
            for (int k = 0; k < text.Length; k++) _map.Add(new CharSource { Placeholder = element });
            _lastSpace = kind == SpanKind.LineBreak;
        }

        private string ResolveSrc(string? src) =>
            src == null ? "" : EpubPath.IsExternal(src) ? src : EpubPath.Resolve(walker.DocumentPath, src);

        private void WalkElement(XElement e)
        {
            var name = XmlLoader.Local(e);
            if (SkipNames.Contains(name)) return;
            walker.RecordInlineId(e, blockIndex);
            switch (name)
            {
                case "br":
                    AppendPlaceholder(SpanKind.LineBreak, LineBreakChar.ToString(), null, e);
                    return;
                case "img":
                {
                    var src = ResolveSrc((string?)e.Attribute("src"));
                    AppendPlaceholder(SpanKind.Image, $"[▣ {EpubPath.FileName(src)}]", src, e);
                    return;
                }
                case "svg":
                    foreach (var img in e.Descendants().Where(d => d.Name.LocalName == "image"))
                    {
                        var src = ResolveSrc(XmlLoader.AttrLocal(img, "href"));
                        AppendPlaceholder(SpanKind.Image, $"[▣ {EpubPath.FileName(src)}]", src, e);
                    }
                    return;
                case "ruby":
                {
                    int start = _sb.Length;
                    WalkChildren(e);
                    var reading = string.Concat(e.Descendants().Where(d => d.Name.LocalName == "rt").Select(d => d.Value));
                    AddSpan(SpanKind.Ruby, start, reading);
                    return;
                }
            }

            SpanKind? kind = name switch
            {
                "b" or "strong" => SpanKind.Bold,
                "i" or "em" or "cite" or "var" or "dfn" => SpanKind.Italic,
                "a" when IsNoteRef(e) => SpanKind.NoteRef,
                "a" when e.Attribute("href") != null => SpanKind.Link,
                _ => null,
            };
            int s0 = _sb.Length;
            WalkChildren(e);
            if (kind is { } k) AddSpan(k, s0, k is SpanKind.Link or SpanKind.NoteRef ? (string?)e.Attribute("href") : null);
        }

        private void WalkChildren(XElement e)
        {
            foreach (var n in e.Nodes()) Walk(n);
        }

        private void AddSpan(SpanKind kind, int start, string? data)
        {
            if (_sb.Length > start) _spans.Add(new InlineSpan(kind, start, _sb.Length - start, data));
        }

        public (string Text, List<InlineSpan> Spans, CharSource[] Map) Finish()
        {
            int end = _sb.Length;
            while (end > 0 && _sb[end - 1] == ' ' && _map[end - 1].Node != null) end--;
            var text = _sb.ToString(0, end);
            var spans = _spans
                .Where(s => s.Start < end)
                .Select(s => s.End > end ? s with { Length = end - s.Start } : s)
                .OrderBy(s => s.Start)
                .ToList();
            return (text, spans, _map.Take(end).ToArray());
        }
    }
}
