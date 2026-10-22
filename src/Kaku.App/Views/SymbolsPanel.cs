using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Text;

namespace Kaku.App.Views;

/// <summary>
/// Symbols for translation work: the ones the book uses most, recent ones, and fixed groups.
/// Click inserts at the editor cursor; right-click copies to the clipboard.
/// </summary>
public sealed class SymbolsPanel : ScrollViewer
{
    private static readonly (string Key, string Symbols)[] Groups =
    [
        ("Symbols_Spanish", "— – « » “ ” ‘ ’ ¿ ¡ … · ª º"),
        ("Symbols_Macrons", "ā ī ū ē ō Ā Ī Ū Ē Ō"),
        ("Symbols_Japanese", "「 」 『 』 ・ ー 〜 ※ 〇 々"),
        ("Symbols_Spaces", ""),
        ("Symbols_Other", "° × ÷ ± ½ ¼ ¾ € £ ¥ ★ ☆ ♪ ♥ † ‡ § ¶ © ® ™ → ← ↑ ↓ • ◆ ○ ●"),
    ];

    private static readonly Dictionary<string, string> Names = new()
    {
        ["—"] = "raya", ["–"] = "semirraya", ["«"] = "comilla angular de apertura", ["»"] = "comilla angular de cierre",
        ["“"] = "comilla inglesa de apertura", ["”"] = "comilla inglesa de cierre", ["‘"] = "comilla simple de apertura",
        ["’"] = "comilla simple de cierre / apóstrofo", ["¿"] = "apertura de interrogación", ["¡"] = "apertura de exclamación",
        ["…"] = "puntos suspensivos", ["·"] = "punto medio", ["ª"] = "ordinal femenino", ["º"] = "ordinal masculino",
        ["「"] = "comilla japonesa de apertura", ["」"] = "comilla japonesa de cierre", ["『"] = "comilla japonesa doble de apertura",
        ["』"] = "comilla japonesa doble de cierre", ["・"] = "punto japonés", ["ー"] = "alargamiento (chōonpu)", ["〜"] = "onda",
        [" "] = "espacio de no separación", [" "] = "espacio estrecho de no separación", [" "] = "espacio fino",
        ["　"] = "espacio japonés (ideográfico)", ["\""] = "comilla recta", ["'"] = "apóstrofo recto", ["-"] = "guion",
    };

    private readonly StackPanel _root = new() { Margin = new Thickness(8, 4, 8, 8) };
    private readonly WrapPanel _used = new();
    private readonly WrapPanel _recent = new();
    private readonly TextBlock _usedTitle = Title(T.Get("Symbols_Used"));
    private readonly TextBlock _recentTitle = Title(T.Get("Symbols_Recent"));

    public SymbolsPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = Brushes.White;
        Content = _root;
        _root.Children.Add(new TextBlock { Text = T.Get("Symbols_Help"), Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        _root.Children.Add(_usedTitle);
        _root.Children.Add(_used);
        _root.Children.Add(_recentTitle);
        _root.Children.Add(_recent);
        foreach (var (key, symbols) in Groups)
        {
            _root.Children.Add(Title(T.Get(key)));
            var wrap = new WrapPanel();
            // the spaces group cannot be split on spaces
            var items = key == "Symbols_Spaces" ? [" ", " ", " ", "　"] : symbols.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in items) wrap.Children.Add(MakeButton(s, null));
            _root.Children.Add(wrap);
        }
        SetRecent([]);
        SetBook(null);
    }

    /// <summary>Insert the symbol at the cursor.</summary>
    public event Action<string>? InsertRequested;

    /// <summary>Copied to the clipboard (for the status bar).</summary>
    public event Action<string>? Copied;

    private static TextBlock Title(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)), Margin = new Thickness(0, 8, 0, 3),
    };

    public void SetBook(Book? book)
    {
        _used.Children.Clear();
        var counts = book == null ? [] : SymbolStats.Count(book).Take(40).ToList();
        foreach (var c in counts) _used.Children.Add(MakeButton(c.Symbol, c.Count));
        _usedTitle.Visibility = _used.Visibility = counts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRecent(IReadOnlyList<string> recent)
    {
        _recent.Children.Clear();
        foreach (var s in recent) _recent.Children.Add(MakeButton(s, null));
        _recentTitle.Visibility = _recent.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string Describe(string s)
    {
        var code = string.Join(" ", s.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
        var name = Names.GetValueOrDefault(s);
        return name == null ? code : $"{name} ({code})";
    }

    private static bool IsSpace(string s) => s.Length == 1 && char.IsWhiteSpace(s[0]);

    private Button MakeButton(string symbol, int? count)
    {
        var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(new TextBlock
        {
            Text = IsSpace(symbol) ? "⍽" : symbol,
            FontSize = 18,
            FontFamily = new FontFamily("Georgia, Yu Mincho, Segoe UI Symbol"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = IsSpace(symbol) ? Brushes.SteelBlue : Brushes.Black,
        });
        if (count != null)
            face.Children.Add(new TextBlock { Text = count.Value.ToString("N0", T.Es), FontSize = 9.5, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        var b = new Button
        {
            Content = face,
            MinWidth = 38,
            Height = count != null ? 46 : 36,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            Focusable = false,
            ToolTip = Describe(symbol) + (count != null ? $"\n{T.F("Symbols_Count", count.Value.ToString("N0", T.Es))}" : "") + "\n" + T.Get("Symbols_Tip"),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => InsertRequested?.Invoke(symbol);
        b.MouseRightButtonUp += (_, e) =>
        {
            try
            {
                Clipboard.SetText(symbol);
                Copied?.Invoke(symbol);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // clipboard busy: try again
            }
            e.Handled = true;
        };
        return b;
    }
}
