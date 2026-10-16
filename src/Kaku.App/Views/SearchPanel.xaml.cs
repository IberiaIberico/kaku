using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.App.ViewModels;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Search;

namespace Kaku.App.Views;

/// <summary>
/// Find / replace in the current chapter or the whole book (bottom panel). The window owns navigation and
/// applying replacements; this panel owns the query, the result list and the replacement preview.
/// </summary>
public partial class SearchPanel : UserControl
{
    private const int MaxHits = 5000;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<SearchHitItem> _items = new();

    /// <summary>Built-in searches for typical translation problems.</summary>
    private static readonly SavedSearch[] BuiltIn =
    [
        new(T.Get("Saved_Japanese"), @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]+", true),
        new(T.Get("Saved_StraightQuotes"), "\"", false),
        new(T.Get("Saved_Guillemets"), "[«»]", true),
        new(T.Get("Saved_CurlyQuotes"), "[“”]", true),
        new(T.Get("Saved_JapaneseQuotes"), "[「」『』]", true),
        new(T.Get("Saved_HyphenDialogue"), @"^[-–]\s?", true),
        new(T.Get("Saved_ThreeDots"), @"\.\.\.", true),
        new(T.Get("Saved_SpaceBeforePunct"), @" [,.;:!?»”)]", true),
        new(T.Get("Saved_RepeatedWord"), @"\b(\w+)\s+\1\b", true),
        new(T.Get("Saved_ModelTokens"), @"<\|[^|>]*\|>|<\|channel>", true),
    ];

    public SearchPanel()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Run();
        };
    }

    public Book? Book { get; set; }
    public AppSettings? Settings { get; set; }

    /// <summary>Index of the chapter shown in the editor (for "en el capítulo").</summary>
    public Func<int>? CurrentChapter { get; set; }

    /// <summary>A result was chosen: show it in the editor. The bool says whether the editor should take focus.</summary>
    public event Action<SearchHit, bool>? HitActivated;

    /// <summary>Apply these replacements (the window records undo and refreshes views).</summary>
    public event Action<SearchQuery, List<SearchHit>, string>? ReplaceRequested;

    public event Action? UndoReplaceRequested;

    /// <summary>New results (for highlighting in the editors).</summary>
    public event Action<IReadOnlyList<SearchHit>>? ResultsChanged;

    public SearchQuery? Query { get; private set; }
    public IReadOnlyList<SearchHit> Hits => _items.Select(i => i.Hit).ToList();

    public bool CanUndoReplace
    {
        set => UndoReplaceButton.IsEnabled = value;
    }

    // ================================================================== opening

    /// <summary>Ctrl+F / Ctrl+Mayús+F / Ctrl+H: focus the right box, optionally with the selected text.</summary>
    public void Activate(bool wholeBook, string? initial, bool replace)
    {
        WhereBox.SelectedIndex = wholeBook ? 1 : 0;
        if (!string.IsNullOrEmpty(initial) && !initial.Contains('\n')) FindBox.Text = initial;
        var box = replace ? ReplaceBox : FindBox;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
        Run();
    }

    public void Clear()
    {
        _items = new();
        Bind();
        CountText.Text = "";
    }

    // ================================================================== searching

    private SearchQuery BuildQuery() => new(
        FindBox.Text,
        CaseOption.IsChecked == true,
        WordOption.IsChecked == true,
        AccentOption.IsChecked == true,
        RegexOption.IsChecked == true,
        ScopeBox.SelectedIndex == 1 ? SearchScope.Code : SearchScope.Text);

    /// <summary>Runs the search again (after edits, or when the query changes).</summary>
    public void Run()
    {
        if (Book == null)
        {
            Clear();
            return;
        }
        var q = BuildQuery();
        Query = q;
        if (q.Pattern.Length == 0)
        {
            Clear();
            ResultsChanged?.Invoke([]);
            return;
        }
        IReadOnlyCollection<int>? chapters = WhereBox.SelectedIndex == 0 && CurrentChapter?.Invoke() is int c and >= 0 ? [c] : null;
        var result = BookSearch.Find(Book, q, chapters, MaxHits);
        if (result.Error != null && result.Hits.Count == 0)
        {
            Clear();
            CountText.Text = T.F("Search_Error", result.Error);
            ResultsChanged?.Invoke([]);
            return;
        }
        _items = result.Hits.Select(h => new SearchHitItem(Book, h)).ToList();
        UpdateReplacements();
        Bind();
        int files = _items.Select(i => i.Hit.Path).Distinct().Count();
        CountText.Text = _items.Count == 0 ? T.Get("Search_None")
            : T.F(result.Truncated ? "Search_CountTruncated" : "Search_Count", T.N0(_items.Count), files);
        ResultsChanged?.Invoke(result.Hits);
    }

    private void Bind()
    {
        var source = (CollectionViewSource)Resources["Grouped"];
        source.Source = _items;
        source.View?.Refresh();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Restart();

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Restart();
    }

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    // ================================================================== replacement preview

    private void OnReplaceChanged(object sender, TextChangedEventArgs e) => UpdateReplacements();

    /// <summary>Shows next to every match what it will become (regex groups expanded).</summary>
    private void UpdateReplacements()
    {
        bool replacing = ReplaceBox.Text.Length > 0 || ReplaceBox.IsKeyboardFocusWithin;
        var q = Query;
        var regex = q?.UseRegex == true ? q.Build(out _) : null;
        foreach (var item in _items)
            item.Replacement = !replacing || q == null ? null : regex == null ? ReplaceBox.Text : Preview(item.Hit, q, regex);
    }

    private string Preview(SearchHit hit, SearchQuery q, System.Text.RegularExpressions.Regex regex)
    {
        string? original = null;
        if (Book!.ChapterByPath(hit.Path) is { } c)
            original = hit.Block >= 0 ? c.Blocks[hit.Block].WordText : c.SourceText;
        else if (Book.Package.Find(hit.Path) is { } entry)
            original = XmlLoader.DecodeText(entry.Data);
        if (original == null) return ReplaceBox.Text;
        var text = q.IgnoreAccents ? BookSearch.FoldAccents(original) : original;
        var m = regex.Match(text, Math.Min(hit.Start, text.Length));
        return m.Success && m.Index == hit.Start ? BookSearch.Expand(ReplaceBox.Text, m, original) : ReplaceBox.Text;
    }

    // ================================================================== replacing

    private void OnReplaceOneClick(object sender, RoutedEventArgs e)
    {
        if (Query == null) return;
        var item = Results.SelectedItem as SearchHitItem ?? _items.FirstOrDefault();
        if (item == null) return;
        int index = _items.IndexOf(item);
        ReplaceRequested?.Invoke(Query, [item.Hit], ReplaceBox.Text);
        Run();
        if (_items.Count > 0) SelectIndex(Math.Min(index, _items.Count - 1), focusEditor: false);
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        if (Query == null) return;
        var chosen = _items.Where(i => i.Include).Select(i => i.Hit).ToList();
        if (chosen.Count == 0) return;
        int files = chosen.Select(h => h.Path).Distinct().Count();
        var answer = MessageBox.Show(Window.GetWindow(this)!, T.F("Search_ConfirmReplaceAll", T.N0(chosen.Count), files, ReplaceBox.Text),
                "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        ReplaceRequested?.Invoke(Query, chosen, ReplaceBox.Text);
        Run();
    }

    private void OnUndoReplaceClick(object sender, RoutedEventArgs e)
    {
        UndoReplaceRequested?.Invoke();
        Run();
    }

    private void OnAllCheckClick(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Include = AllCheck.IsChecked == true;
    }

    // ================================================================== result navigation

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is SearchHitItem item) HitActivated?.Invoke(item.Hit, false);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is SearchHitItem item) HitActivated?.Invoke(item.Hit, true);
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Results.SelectedItem is SearchHitItem item)
        {
            HitActivated?.Invoke(item.Hit, true);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Results.SelectedItem is SearchHitItem toggle && toggle.HasReplacement)
        {
            toggle.Include = !toggle.Include;
            e.Handled = true;
        }
    }

    /// <summary>F3 / Mayús+F3: next/previous result (in book order).</summary>
    public void Step(int direction)
    {
        if (_items.Count == 0)
        {
            Run();
            if (_items.Count == 0) return;
        }
        int i = Results.SelectedItem is SearchHitItem cur ? _items.IndexOf(cur) : direction > 0 ? -1 : 0;
        SelectIndex((i + direction + _items.Count) % _items.Count, focusEditor: true);
    }

    private void SelectIndex(int i, bool focusEditor)
    {
        var item = _items[i];
        Results.SelectionChanged -= OnResultSelected;
        Results.SelectedItem = item;
        Results.SelectionChanged += OnResultSelected;
        Results.ScrollIntoView(item);
        HitActivated?.Invoke(item.Hit, focusEditor);
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _debounce.Stop();
            if (Query?.Pattern != FindBox.Text) Run();
            Step(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
            FindBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _items.Count > 0)
        {
            Results.SelectedIndex = 0;
            (Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void OnReplaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnReplaceOneClick(sender, e);
            e.Handled = true;
        }
    }

    // ================================================================== saved searches

    private void OnSavedClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SavedButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var s in BuiltIn) menu.Items.Add(SavedItem(s));
        var mine = Settings?.SavedSearches ?? new();
        if (mine.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var s in mine) menu.Items.Add(SavedItem(s));
        }
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = T.Get("Search_SaveCurrent"), IsEnabled = FindBox.Text.Length > 0 };
        save.Click += (_, _) => SaveCurrent();
        menu.Items.Add(save);
        if (mine.Count > 0)
        {
            var clear = new MenuItem { Header = T.Get("Search_ClearSaved") };
            clear.Click += (_, _) =>
            {
                Settings!.SavedSearches.Clear();
                Settings.Save();
            };
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    private MenuItem SavedItem(SavedSearch s)
    {
        var item = new MenuItem { Header = s.Name.Replace("_", "__"), ToolTip = s.Pattern };
        item.Click += (_, _) =>
        {
            RegexOption.IsChecked = s.Regex;
            CaseOption.IsChecked = s.MatchCase;
            WordOption.IsChecked = false;
            AccentOption.IsChecked = false;
            ScopeBox.SelectedIndex = s.Code ? 1 : 0;
            WhereBox.SelectedIndex = 1;
            FindBox.Text = s.Pattern;
            _debounce.Stop();
            Run();
        };
        return item;
    }

    private void SaveCurrent()
    {
        if (Settings == null) return;
        var name = InputDialog.Ask(Window.GetWindow(this)!, T.Get("Search_SaveTitle"), T.Get("Search_SavePrompt"), FindBox.Text);
        if (string.IsNullOrWhiteSpace(name)) return;
        Settings.SavedSearches.RemoveAll(s => s.Name == name);
        Settings.SavedSearches.Add(new SavedSearch(name.Trim(), FindBox.Text, RegexOption.IsChecked == true, CaseOption.IsChecked == true, ScopeBox.SelectedIndex == 1));
        Settings.Save();
    }
}
