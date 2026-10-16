using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Kaku.App.Editor;

/// <summary>Highlights search matches. Segments are resolved lazily (block + offset, or plain offsets).</summary>
public sealed class SearchHighlightRenderer(Func<IEnumerable<(int Offset, int Length)>> segments) : IBackgroundRenderer
{
    private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD3, 0x4D)));
    private static readonly Pen Border = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE0, 0x9A, 0x00)), 1));

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        int first = textView.VisualLines[0].FirstDocumentLine.Offset;
        int last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
        bool any = false;
        foreach (var (offset, length) in segments())
        {
            if (offset + length < first || offset > last || length <= 0) continue;
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = length });
            builder.CloseFigure();
            any = true;
        }
        if (any && builder.CreateGeometry() is { } g) dc.DrawGeometry(Fill, Border, g);
    }

    internal static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Pen p)
    {
        p.Freeze();
        return p;
    }
}

/// <summary>Red wavy underline under misspelled words, for the visible lines only (cached per line text).</summary>
public sealed class SpellingRenderer(Func<int, string?> wordTextOfLine) : IBackgroundRenderer
{
    private static readonly Pen Wave = CreatePen();
    private readonly Dictionary<string, List<Kaku.Core.Checks.Misspelling>> _cache = new(StringComparer.Ordinal);
    private int _version = -1;

    public bool Enabled { get; set; } = true;

    public KnownLayer Layer => KnownLayer.Selection;

    private static Pen CreatePen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), 1.1);
        p.Freeze();
        return p;
    }

    /// <summary>Misspellings of a line (null if the checker is not ready).</summary>
    public List<Kaku.Core.Checks.Misspelling>? For(int lineNumber)
    {
        var checker = Services.SpellService.Checker;
        if (checker == null || wordTextOfLine(lineNumber) is not { } text) return null;
        if (checker.Version != _version)
        {
            _cache.Clear();
            _version = checker.Version;
        }
        if (!_cache.TryGetValue(text, out var list))
        {
            if (_cache.Count > 4000) _cache.Clear();
            _cache[text] = list = checker.Check(text);
        }
        return list;
    }

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!Enabled || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            var line = vl.FirstDocumentLine;
            if (For(line.LineNumber) is not { Count: > 0 } errors) continue;
            foreach (var e in errors)
            {
                if (e.Start + e.Length > line.Length) continue;
                var seg = new TextSegment { StartOffset = line.Offset + e.Start, Length = e.Length };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        double y = r.Bottom - 1.5, x = r.Left;
                        ctx.BeginFigure(new Point(x, y), false, false);
                        bool up = true;
                        while (x < r.Right)
                        {
                            x = Math.Min(x + 2.5, r.Right);
                            ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                            up = !up;
                        }
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, Wave, g);
                }
            }
        }
    }
}

/// <summary>Bookmarked paragraphs: a bar at the left edge and a light tint.</summary>
public sealed class BookmarkRenderer(Func<ICollection<int>> lineNumbers) : IBackgroundRenderer
{
    private static readonly Brush Bar = SearchHighlightRenderer.Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush Tint = SearchHighlightRenderer.Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0x25, 0x63, 0xEB)));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        var lines = lineNumbers();
        if (lines.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            if (!lines.Contains(vl.FirstDocumentLine.LineNumber)) continue;
            double y = vl.VisualTop - textView.VerticalOffset;
            dc.DrawRectangle(Tint, null, new Rect(0, y, textView.ActualWidth, vl.Height));
            dc.DrawRectangle(Bar, null, new Rect(0, y, 4, vl.Height));
        }
    }
}
