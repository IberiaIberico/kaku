using System.Windows;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Navigation;

namespace Kaku.App.Views;

/// <summary>Ctrl+G: jump to a %, word number, chapter or chapter:paragraph, with a live preview of the target.</summary>
public partial class GoToDialog : Window
{
    private readonly Book _book;
    private static string _lastInput = "";

    public GoToDialog(Window owner, Book book)
    {
        InitializeComponent();
        Owner = owner;
        _book = book;
        Input.Text = _lastInput;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public GoToTarget? Target { get; private set; }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Target = GoToParser.Parse(_book, Input.Text);
        GoButton.IsEnabled = Target != null;
        Result.Text = Input.Text.Trim().Length == 0 ? "" : Target == null ? T.Get("GoTo_Invalid") : Describe(_book, Target.Location);
    }

    public static string Describe(Book book, TextLocation loc)
    {
        var c = book.Chapters[loc.Chapter];
        long w = book.WordIndexAt(loc);
        return T.F("GoTo_Describe", loc.Chapter + 1, c.Title, loc.Block + 1, T.N0(Math.Min(w + 1, book.TotalWords)), T.N1(book.PercentAt(w)));
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Target == null) e.Handled = true;
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (Target == null) return;
        _lastInput = Input.Text;
        DialogResult = true;
    }
}
