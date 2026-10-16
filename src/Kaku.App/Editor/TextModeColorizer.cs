using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kaku.Core;
using Kaku.Core.Text;

namespace Kaku.App.Editor;

/// <summary>
/// Renders text mode: one line per block, inline markup shown as formatting (bold, italics, links,
/// ruby, note refs) and placeholders (images, line breaks) as grey chips.
/// </summary>
public sealed class TextModeColorizer(Chapter chapter) : DocumentColorizingTransformer
{
    private static readonly Brush HeadingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F)));
    private static readonly Brush NoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)));
    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x5F, 0xB4)));
    private static readonly Brush RubyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x00)));
    private static readonly Brush ChipForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66)));
    private static readonly Brush ChipBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)));
    private static readonly Brush NoteRefBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xEA, 0xFB)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int bi = line.LineNumber - 1;
        if (bi >= chapter.Blocks.Count || line.Length == 0) return;
        var block = chapter.Blocks[bi];
        int start = line.Offset, end = line.EndOffset;

        if (block.Kind == BlockKind.Heading)
        {
            double scale = block.HeadingLevel switch { 1 => 1.55, 2 => 1.4, 3 => 1.25, _ => 1.12 };
            ChangeLinePart(start, end, el =>
            {
                el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * scale);
                SetTypeface(el, bold: true, italic: null);
                el.TextRunProperties.SetForegroundBrush(HeadingBrush);
            });
        }
        else if (block.IsNote)
        {
            ChangeLinePart(start, end, el =>
            {
                el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * 0.9);
                el.TextRunProperties.SetForegroundBrush(NoteBrush);
            });
        }

        foreach (var span in block.Spans)
        {
            int s = start + Math.Min(span.Start, line.Length);
            int e = start + Math.Min(span.End, line.Length);
            if (e <= s) continue;
            switch (span.Kind)
            {
                case SpanKind.Bold:
                    ChangeLinePart(s, e, el => SetTypeface(el, bold: true, italic: null));
                    break;
                case SpanKind.Italic:
                    ChangeLinePart(s, e, el => SetTypeface(el, bold: null, italic: true));
                    break;
                case SpanKind.Link:
                    ChangeLinePart(s, e, el =>
                    {
                        el.TextRunProperties.SetForegroundBrush(LinkBrush);
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    });
                    break;
                case SpanKind.NoteRef:
                    ChangeLinePart(s, e, el =>
                    {
                        el.TextRunProperties.SetForegroundBrush(LinkBrush);
                        el.TextRunProperties.SetBackgroundBrush(NoteRefBackground);
                        el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * 0.8);
                        el.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Superscript);
                    });
                    break;
                case SpanKind.Ruby:
                    ChangeLinePart(s, e, el =>
                    {
                        el.TextRunProperties.SetForegroundBrush(RubyBrush);
                        el.TextRunProperties.SetTextDecorations(DottedUnderline);
                    });
                    break;
                case SpanKind.Image:
                case SpanKind.LineBreak:
                    ChangeLinePart(s, e, el =>
                    {
                        el.TextRunProperties.SetForegroundBrush(ChipForeground);
                        el.TextRunProperties.SetBackgroundBrush(ChipBackground);
                        el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * 0.85);
                    });
                    break;
            }
        }
    }

    private static readonly TextDecorationCollection DottedUnderline = CreateDotted();

    private static TextDecorationCollection CreateDotted()
    {
        var pen = new Pen(RubyBrush, 1) { DashStyle = DashStyles.Dot };
        pen.Freeze();
        var c = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel) };
        c.Freeze();
        return c;
    }

    private static void SetTypeface(VisualLineElement el, bool? bold, bool? italic)
    {
        var tf = el.TextRunProperties.Typeface;
        el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily,
            italic == true ? FontStyles.Italic : tf.Style,
            bold == true ? FontWeights.Bold : tf.Weight,
            tf.Stretch));
    }
}
