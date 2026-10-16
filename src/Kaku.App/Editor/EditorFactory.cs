using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

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
        ed.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(HighlightingExtension(path));
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
        ed.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x22, 0xF0, 0xAA, 0x1E));
        ed.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        ed.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA6, 0xB0));
        return ed;
    }
}
