using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Checks;
using Kaku.Core.Epub;

namespace Kaku.App.Views;

/// <summary>Problems of the chapter or the book: text checks, spelling and EPUB validity; jump, fix, fix all.</summary>
public partial class ProblemsPanel : UserControl
{
    private List<Row> _rows = new();
    private Book? _book;
    private bool _loading;

    public sealed record Row(Problem Problem, string KindText, string WhereText)
    {
        public string Message => Problem.Message;

        public string Icon => Problem.Severity switch { Severity.Error => "●", Severity.Warning => "▲", _ => "i" };

        public Brush IconBrush => Problem.Severity switch
        {
            Severity.Error => Theme.Brush("Error"),
            Severity.Warning => Theme.Brush("Caution"),
            _ => Theme.Brush("Info"),
        };

        // the context with the problem highlighted
        private (string, string, string) Parts()
        {
            var p = Problem;
            if (p.Context.Length == 0 || p.Length == 0) return (p.Context.Length > 0 ? p.Context : p.Message, "", "");
            var hit = p.Word ?? "";
            int i = hit.Length > 0 ? p.Context.IndexOf(hit, StringComparison.Ordinal) : -1;
            if (i < 0) return (p.Message + " — " + p.Context, "", "");
            return (p.Context[..i], hit, p.Context[(i + hit.Length)..]);
        }

        public string Before => Parts().Item1;
        public string Hit => Parts().Item2;
        public string After => Parts().Item3;
        public string FixText => Problem.Fix is { } f ? "  → " + (f.Insert.Length == 0 ? T.Get("Problems_FixDelete") : $"«{f.Insert}»") : "";
    }

    public ProblemsPanel()
    {
        InitializeComponent();
        UpdateButtons();
    }

    public event Action? RefreshRequested;
    public event Action<Problem>? ProblemActivated;
    public event Action<List<Problem>>? FixRequested;
    public event Action? EpubcheckRequested;
    public event Action<Problem, string>? SuggestionChosen;
    public event Action<bool>? SpellingToggled;

    public bool WholeBook => ScopeBox.SelectedIndex == 1;
    public bool Spelling
    {
        get => SpellingCheck.IsChecked == true;
        set => SpellingCheck.IsChecked = value;
    }

    public void SetBusy(string text) => SummaryText.Text = text;

    public void SetProblems(Book? book, List<Problem> problems)
    {
        _book = book;
        if (book == null)
        {
            _rows = new();
            Bind();
            return;
        }
        _rows = problems
            .OrderBy(p => p.Severity)
            .ThenBy(p => p.Kind)
            .ThenBy(p => p.Chapter)
            .ThenBy(p => p.Block)
            .ThenBy(p => p.Start)
            .Select(p => new Row(p, T.Get("Check_Kind_" + p.Kind), Where(book, p)))
            .ToList();
        // the kind filter, with counts
        var keep = (KindBox.SelectedItem as ComboBoxItem)?.Tag as CheckKind?;
        _loading = true;
        KindBox.Items.Clear();
        KindBox.Items.Add(new ComboBoxItem { Content = T.F("Problems_AllKinds", _rows.Count), Tag = null });
        foreach (var g in _rows.GroupBy(r => r.Problem.Kind).OrderBy(g => g.Key))
            KindBox.Items.Add(new ComboBoxItem { Content = $"{T.Get("Check_Kind_" + g.Key)} ({g.Count()})", Tag = g.Key });
        KindBox.SelectedItem = KindBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (CheckKind?)i.Tag == keep) ?? KindBox.Items[0];
        _loading = false;
        Bind();
    }

    private static string Where(Book book, Problem p)
    {
        if (p.Chapter >= 0 && p.Chapter < book.Chapters.Count)
            return T.F("Problems_Where", p.Chapter + 1, book.Chapters[p.Chapter].Title, p.Block + 1);
        return p.Path == null ? "" : p.Line > 0 ? $"{EpubPath.FileName(p.Path)}:{p.Line}" : EpubPath.FileName(p.Path);
    }

    private CheckKind? KindFilter => (KindBox.SelectedItem as ComboBoxItem)?.Tag as CheckKind?;

    private void Bind()
    {
        var kind = KindFilter;
        var shown = kind == null ? _rows : _rows.Where(r => r.Problem.Kind == kind).ToList();
        List.ItemsSource = shown.Take(5000).ToList();
        int e = _rows.Count(r => r.Problem.Severity == Severity.Error), w = _rows.Count(r => r.Problem.Severity == Severity.Warning);
        SummaryText.Text = _rows.Count == 0 ? T.Get("Problems_None") : T.F("Problems_Summary", e, w, _rows.Count - e - w)
                           + (shown.Count > 5000 ? " " + T.Get("Problems_Truncated") : "");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var kind = KindFilter;
        var fixable = _rows.Where(r => r.Problem.Fix != null && (kind == null || r.Problem.Kind == kind)).ToList();
        FixAllButton.Content = kind == null ? T.F("Problems_FixAllKinds", fixable.Count) : T.F("Problems_FixAll", fixable.Count);
        FixAllButton.IsEnabled = fixable.Count > 0;
        FixButton.IsEnabled = List.SelectedItems.Cast<Row>().Any(r => r.Problem.Fix != null);
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshRequested?.Invoke();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Bind();
    }

    private void OnSpellingClick(object sender, RoutedEventArgs e) => SpellingToggled?.Invoke(Spelling);

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnFix(object sender, RoutedEventArgs e)
    {
        var list = List.SelectedItems.Cast<Row>().Where(r => r.Problem.Fix != null).Select(r => r.Problem).ToList();
        if (list.Count > 0) FixRequested?.Invoke(list);
    }

    private void OnFixAll(object sender, RoutedEventArgs e)
    {
        var kind = KindFilter;
        var list = _rows.Where(r => r.Problem.Fix != null && (kind == null || r.Problem.Kind == kind)).Select(r => r.Problem).ToList();
        if (list.Count == 0) return;
        var label = kind == null ? T.Get("Problems_AllFixable") : T.Get("Check_Kind_" + kind);
        if (MessageBox.Show(Window.GetWindow(this)!, T.F("Problems_ConfirmFixAll", list.Count, label), "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            FixRequested?.Invoke(list);
    }

    private void OnEpubcheck(object sender, RoutedEventArgs e) => EpubcheckRequested?.Invoke();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is Row r) ProblemActivated?.Invoke(r.Problem);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is Row r)
        {
            ProblemActivated?.Invoke(r.Problem);
            e.Handled = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        if (List.SelectedItem is not Row r)
        {
            e.Handled = true;
            return;
        }
        var p = r.Problem;
        void Add(string header, Action a, bool bold = false)
        {
            var mi = new MenuItem { Header = header.Replace("_", "__"), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add(T.Get("Problems_Go"), () => ProblemActivated?.Invoke(p));
        if (p.Fix != null) Add(T.Get("Problems_Fix"), () => FixRequested?.Invoke([p]));
        if (p.Kind == CheckKind.Spelling && p.Word != null && SpellService.Checker is { } checker)
        {
            menu.Items.Add(new Separator());
            var sugs = checker.Suggest(p.Word);
            foreach (var s in sugs) Add(s, () => SuggestionChosen?.Invoke(p, s), bold: true);
            if (sugs.Count == 0) menu.Items.Add(new MenuItem { Header = T.Get("Spell_NoSuggestions"), IsEnabled = false });
            menu.Items.Add(new Separator());
            Add(T.F("Spell_Add", p.Word), () =>
            {
                SpellService.AddToDictionary(p.Word);
                RemoveWord(p.Word);
            });
            Add(T.Get("Spell_Ignore"), () =>
            {
                SpellService.Ignore(p.Word);
                RemoveWord(p.Word);
            });
        }
    }

    /// <summary>A word was accepted: drop its rows without re-running the checks.</summary>
    private void RemoveWord(string word)
    {
        _rows = _rows.Where(r => !(r.Problem.Kind == CheckKind.Spelling && string.Equals(r.Problem.Word, word, StringComparison.OrdinalIgnoreCase))).ToList();
        if (_book != null) SetProblems(_book, _rows.Select(r => r.Problem).ToList());
    }
}
