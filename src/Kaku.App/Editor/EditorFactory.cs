using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kaku.App.Services;
using Control = System.Windows.Controls.Control;

namespace Kaku.App.Editor;

/// <summary>Creates AvalonEdit instances with Kaku's defaults.</summary>
public static class EditorFactory
{
    public static readonly FontFamily ProseFont = new("Georgia, Yu Mincho, MS Mincho");
    public static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, MS Gothic");

    public static TextEditor CreateProse()
    {
        var ed = Create();
        ed.FontFamily = ProseFont;
        ed.FontSize = 16;
        ed.Padding = new Thickness(18, 10, 18, 10);
        ed.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        return ed;
    }

    public static TextEditor CreateCode(string path)
    {
        var ed = Create();
        ed.FontFamily = CodeFont;
        ed.FontSize = 13;
        if (HighlightingManager.Instance.GetDefinitionByExtension(HighlightingExtension(path)) is { } definition)
            ed.TextArea.TextView.LineTransformers.Insert(0, new ThemedHighlightingColorizer(definition));
        return ed;
    }

    private static string HighlightingExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xhtml" or ".html" or ".htm" or ".opf" or ".ncx" or ".svg" or ".smil" => ".xml",
            _ => ext,
        };
    }

    private static TextEditor Create()
    {
        var ed = new TextEditor
        {
            IsReadOnly = true, // M1: viewing only; editing arrives in M2
            WordWrap = true,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
        };
        ed.Options.EnableHyperlinks = false;
        ed.Options.EnableEmailHyperlinks = false;
        ed.Options.HighlightCurrentLine = true;
        ed.Options.ShowBoxForControlCharacters = true;
        ed.TextArea.SelectionCornerRadius = 0;
        ed.TextArea.TextView.SetResourceReference(TextView.CurrentLineBackgroundProperty, "Editor.CurrentLine");
        ed.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        ed.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Editor.LineNumbers");
        ed.SetResourceReference(Control.BackgroundProperty, "Editor.Bg");
        ed.SetResourceReference(Control.ForegroundProperty, "Editor.Fg");
        return ed;
    }
}

/// <summary>Syntax highlighting whose colors are lightened in the dark theme (the built-in definitions are made for white).</summary>
public sealed class ThemedHighlightingColorizer(IHighlightingDefinition definition) : HighlightingColorizer(definition)
{
    private static readonly Dictionary<Color, Brush> Lightened = new();

    protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
    {
        base.ApplyColorToElement(element, color);
        if (Theme.IsDark && color.Foreground != null && element.TextRunProperties.ForegroundBrush is SolidColorBrush b)
            element.TextRunProperties.SetForegroundBrush(Lighten(b.Color));
    }

    /// <summary>Keeps the hue, moves the lightness to the bright end: dark blue becomes light blue.</summary>
    private static Brush Lighten(Color c)
    {
        if (Lightened.TryGetValue(c, out var cached)) return cached;
        double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        double h = d == 0 ? 0 : max == r ? ((g - bl) / d + 6) % 6 : max == g ? (bl - r) / d + 2 : (r - g) / d + 4;
        double nl = 0.62 + (1 - l) * 0.2;
        double chroma = (1 - Math.Abs(2 * nl - 1)) * Math.Min(s, 0.75);
        double x = chroma * (1 - Math.Abs(h % 2 - 1)), m = nl - chroma / 2;
        var (r1, g1, b1) = (int)h switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var brush = new SolidColorBrush(Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255)));
        brush.Freeze();
        return Lightened[c] = brush;
    }
}
