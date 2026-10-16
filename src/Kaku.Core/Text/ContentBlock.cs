using System.Xml.Linq;

namespace Kaku.Core.Text;

public enum BlockKind { Paragraph, Heading, Image }

public enum SpanKind { Bold, Italic, Link, NoteRef, Ruby, Image, LineBreak }

/// <summary>Inline formatting inside a block's visible text. Data = href, ruby reading or image path.</summary>
public readonly record struct InlineSpan(SpanKind Kind, int Start, int Length, string? Data = null)
{
    public int End => Start + Length;

    /// <summary>Placeholders stand for non-text content; their characters are not words.</summary>
    public bool IsPlaceholder => Kind is SpanKind.Image or SpanKind.LineBreak;
}

/// <summary>
/// One translatable/editable block of a chapter (p, h1–h6, li, …) as seen in text mode:
/// visible text with tags removed, plus where it came from in the XHTML.
/// </summary>
public sealed class ContentBlock
{
    public int Index { get; init; }
    public required string Tag { get; init; }
    public BlockKind Kind { get; init; }
    public int HeadingLevel { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<InlineSpan> Spans { get; init; } = [];
    /// <summary>Source element (the parent container for loose inline content).</summary>
    public XElement? Element { get; init; }
    /// <summary>1-based line of the block in the XHTML source (0 if unknown).</summary>
    public int SourceLine { get; init; }
    /// <summary>1-based column of the block start in the XHTML source (0 if unknown).</summary>
    public int SourceColumn { get; init; }
    /// <summary>Block belongs to a footnote/endnote: shown, but excluded from word positions.</summary>
    public bool IsNote { get; init; }

    /// <summary>Text with placeholder characters blanked, used for word counting.</summary>
    public string WordText { get; init; } = "";
    public int WordCount { get; init; }
    /// <summary>Number of (non-note) words in the chapter before this block.</summary>
    public int WordStart { get; set; }

    /// <summary>Loose inline content of a container (no element of its own): not splittable or mergeable.</summary>
    public bool IsAnonymous => Tag == "#text";

    /// <summary>Offset of the block in the chapter source (start tag of its element).</summary>
    public int SourceOffset { get; internal set; }

    /// <summary>Where each character of <see cref="Text"/> comes from in the DOM (for editing).</summary>
    internal CharSource[] Map { get; init; } = [];
}

/// <summary>Origin of one visible character: a range of an XText node, or a placeholder element (img, br, svg).</summary>
internal sealed class CharSource
{
    public XText? Node;
    public int Start;
    public int End;
    public XNode? Placeholder;
    /// <summary>Further collapsed whitespace (in other text nodes) rendered as this same space.</summary>
    public List<(XText Node, int Start, int End)>? Extra;
}
