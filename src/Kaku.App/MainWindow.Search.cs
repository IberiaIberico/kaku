using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kaku.App.Editor;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.App.ViewModels;
using Kaku.App.Views;
using Kaku.Core;
using Kaku.Core.Editing;
using Kaku.Core.Navigation;
using Kaku.Core.Search;

namespace Kaku.App;

/// <summary>Search panel, "buscar pasaje", bookmarks and back/forward history.</summary>
public partial class MainWindow
{
    private readonly Dictionary<Chapter, EditHistory> _histories = new();
    private readonly Dictionary<Chapter, int> _blockCounts = new();
    private readonly NavigationHistory _nav = new();
    private readonly BookmarkStore _bookmarkStore = BookmarkStore.Load();
    private readonly ObservableCollection<BookmarkItem> _bookmarks = new();
    private readonly DispatcherTimer _searchRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private PassageFinder? _passageFinder;
    private ReplaceOutcome? _lastReplace;

    private void InitSearch()
    {
        Search.Settings = _settings;
        Search.CurrentChapter = () => ActiveTab?.Editor?.Chapter.Index ?? _currentChapter;
        Search.HitActivated += OnHitActivated;
        Search.ReplaceRequested += OnReplaceRequested;
        Search.UndoReplaceRequested += OnUndoReplace;
        Search.ResultsChanged += hits =>
        {
            foreach (var t in _tabs) t.Editor?.SetSearchHits(hits);
        };
        _searchRefreshTimer.Tick += (_, _) =>
        {
            _searchRefreshTimer.Stop();
            if (BottomPanel.Visibility == Visibility.Visible && Search.Query != null) Search.Run();
        };
        BookmarkList.ItemsSource = _bookmarks;
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            else if (e.ChangedButton == MouseButton.XButton2) GoForward();
        };
    }

    private EditHistory HistoryFor(Chapter c)
    {
        if (!_histories.TryGetValue(c, out var h)) _histories[c] = h = new EditHistory();
        return h;
    }

    /// <summary>Book opened or closed: reset per-book search, bookmarks and history.</summary>
    private void ResetSearchState(Book? book)
    {
        _histories.Clear();
        _blockCounts.Clear();
        _nav.Clear();
        _passageFinder = null;
        _lastReplace = null;
        Search.CanUndoReplace = false;
        Search.Book = book;
        Search.Clear();
        NotesView.Load(null);
        _epubcheckProblems = new();
        _checksCts?.Cancel();
        Problems.SetProblems(null, []);
        _noteCache = null;
        LoadBookmarks();
    }

    // ================================================================== bottom panel

    private void SetBottomPanel(bool show)
    {
        if (!show && BottomRow.ActualHeight > 40) _settings.BottomPanelHeight = BottomRow.ActualHeight;
        _settings.ShowBottomPanel = show;
        BottomPanel.Visibility = BottomSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BottomRow.Height = new GridLength(show ? Math.Max(120, _settings.BottomPanelHeight) : 0);
        BottomSplitterRow.Height = new GridLength(show ? 4 : 0);
        if (!show)
        {
            foreach (var t in _tabs) t.Editor?.SetSearchHits([]);
            ActiveTab?.Editor?.FocusEditor();
        }
        else if (Search.Query != null)
        {
            foreach (var t in _tabs) t.Editor?.SetSearchHits(Search.Hits);
        }
    }

    private void OnCloseBottomClick(object sender, RoutedEventArgs e) => SetBottomPanel(false);

    private void OnToggleBottomClick(object sender, RoutedEventArgs e) => SetBottomPanel(BottomPanel.Visibility != Visibility.Visible);

    private void OpenSearch(bool wholeBook, bool replace)
    {
        if (_book == null) return;
        string? selected = null;
        if (ActiveTab?.Editor is { } ed)
            selected = ed.IsCodeMode ? null : ed.Prose.SelectedText;
        SetBottomPanel(true);
        BottomTabs.SelectedItem = SearchTab;
        Search.Activate(wholeBook, selected, replace);
    }

    private void OnFindChapterClick(object sender, RoutedEventArgs e) => OpenSearch(wholeBook: false, replace: false);

    private void OnFindBookClick(object sender, RoutedEventArgs e) => OpenSearch(wholeBook: true, replace: false);

    private void OnReplaceMenuClick(object sender, RoutedEventArgs e) => OpenSearch(wholeBook: true, replace: true);

    private void OnNextHitClick(object sender, RoutedEventArgs e) => StepHit(+1);

    private void OnPrevHitClick(object sender, RoutedEventArgs e) => StepHit(-1);

    private void StepHit(int direction)
    {
        if (Search.Query == null)
        {
            OpenSearch(wholeBook: true, replace: false);
            return;
        }
        SetBottomPanel(true);
        BottomTabs.SelectedItem = SearchTab;
        Search.Step(direction);
    }

    // ================================================================== results → editor

    private void OnHitActivated(SearchHit hit, bool focusEditor)
    {
        if (_book == null) return;
        RecordJump();
        if (hit.Chapter >= 0)
        {
            var editor = OpenChapter(hit.Chapter, pin: false, location: null, focus: focusEditor);
            editor.SetSearchHits(Search.Hits);
            editor.SelectHit(hit);
        }
        else
        {
            OpenFileByPath(hit.Path, pin: true);
            if (ActiveTab?.View is ICSharpCode.AvalonEdit.TextEditor code && hit.Start + hit.Length <= code.Document.TextLength)
            {
                code.Select(hit.Start, hit.Length);
                code.ScrollTo(hit.Line, 1);
                if (focusEditor) code.Focus();
            }
        }
    }

    // ================================================================== replace

    private void OnReplaceRequested(SearchQuery query, List<SearchHit> hits, string replacement)
    {
        if (_book == null) return;
        FlushAllEditors();
        ReplaceOutcome outcome;
        try
        {
            outcome = BookSearch.Replace(_book, query, hits, replacement);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, T.F("Error_Unexpected", ex.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // each chapter's own undo history gets the step too (Ctrl+Z in that chapter)
        foreach (var (chapter, before) in outcome.ChapterUndo)
            HistoryFor(chapter).Record(new EditSnapshot(before, new TextLocation(chapter.Index, 0, 0), 0, false), null);
        _lastReplace = outcome;
        Search.CanUndoReplace = outcome.ChapterUndo.Count + outcome.FileUndo.Count > 0;
        AfterBulkChange(outcome.ChapterUndo.Select(u => u.Chapter), outcome.FileUndo.Select(u => u.Path));
        StatusText.Text = outcome.Skipped == 0
            ? T.F("Status_Replaced", T.N0(outcome.Replaced))
            : T.F("Status_ReplacedSkipped", T.N0(outcome.Replaced), T.N0(outcome.Skipped));
    }

    private void OnUndoReplace()
    {
        if (_book == null || _lastReplace is not { } r) return;
        foreach (var (chapter, before) in r.ChapterUndo)
        {
            HistoryFor(chapter).Record(new EditSnapshot(chapter.SourceText, new TextLocation(chapter.Index, 0, 0), 0, false), null);
            new ChapterEditor(_book, chapter).SetSource(before);
        }
        foreach (var (path, before) in r.FileUndo) _book.SetFileText(path, before);
        _book.RefreshPositions();
        _book.RefreshTitles();
        _lastReplace = null;
        Search.CanUndoReplace = false;
        AfterBulkChange(r.ChapterUndo.Select(u => u.Chapter), r.FileUndo.Select(u => u.Path));
        StatusText.Text = T.Get("Status_ReplaceUndone");
    }

    /// <summary>Chapters/files changed outside their editors: refresh open views, preview, badges.</summary>
    private void AfterBulkChange(IEnumerable<Chapter> chapters, IEnumerable<string> files)
    {
        var changed = chapters.ToHashSet();
        foreach (var t in _tabs)
        {
            if (t.Editor is { } ed && changed.Contains(ed.Chapter)) ed.ReloadFromModel();
            else if (t.View is ICSharpCode.AvalonEdit.TextEditor code && files.Contains(t.Path) && _book?.Package.Find(t.Path) is { } entry)
                code.Text = Kaku.Core.Epub.XmlLoader.DecodeText(entry.Data);
        }
        foreach (var c in changed) _blockCounts[c] = c.Blocks.Count;
        if (ActiveTab?.Editor is { } active && changed.Contains(active.Chapter)) Preview.Reload(active.Chapter, active.CurrentLocation.Block);
        _recoveryDirty = true;
        foreach (var item in _chapterItems) item.RefreshState();
        UpdateDirtyState();
        UpdateStatus();
    }

    /// <summary>Called on every chapter edit: bookmarks follow split/merge, results refresh.</summary>
    private void OnChapterEditedForSearch(ChapterEditorView view, EditKind kind, int block)
    {
        var c = view.Chapter;
        int before = _blockCounts.TryGetValue(c, out var n) ? n : c.Blocks.Count;
        int delta = c.Blocks.Count - before;
        _blockCounts[c] = c.Blocks.Count;
        if (kind == EditKind.Structure && delta != 0 && block >= 0) ShiftBookmarks(c, block, delta);
        if (BottomPanel.Visibility == Visibility.Visible)
        {
            _searchRefreshTimer.Stop();
            _searchRefreshTimer.Start();
        }
    }

    // ================================================================== find passage

    private void OnFindPassageClick(object sender, RoutedEventArgs e) => ShowFindPassage();

    private void ShowFindPassage()
    {
        if (_book == null) return;
        FlushAllEditors();
        if (_passageFinder == null || !_passageFinder.IsCurrent(_book)) _passageFinder = new PassageFinder(_book);
        string? initial = null;
        if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 10 and < 2000 } clip) initial = clip;
        var dlg = new FindPassageDialog(this, _book, _passageFinder, initial);
        if (dlg.ShowDialog() != true || dlg.Selected is not { } m) return;
        RecordJump();
        var editor = OpenChapter(m.Start.Chapter, pin: false, location: m.Start, focus: true);
        if (m.End.Chapter == m.Start.Chapter) editor.SelectText(m.Start.Block, m.Start.Offset, m.End.Block, m.End.Offset);
    }

    // ================================================================== bookmarks

    private void LoadBookmarks()
    {
        _bookmarks.Clear();
        if (_book != null)
        {
            foreach (var d in _bookmarkStore.For(_book.FilePath))
                if (_book.ChapterByPath(d.ChapterPath) is { } c)
                    _bookmarks.Add(new BookmarkItem(_book, c, d));
        }
        RefreshBookmarks(save: false);
    }

    private void RefreshBookmarks(bool save = true)
    {
        var sorted = _bookmarks.OrderBy(b => b.Chapter.Index).ThenBy(b => b.Block).ToList();
        _bookmarks.Clear();
        foreach (var b in sorted) _bookmarks.Add(b);
        BookmarksEmpty.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _chapterItems) item.BookmarkCount = _bookmarks.Count(b => b.Chapter == item.Chapter);
        foreach (var t in _tabs)
            if (t.Editor is { } ed) ed.SetBookmarks(_bookmarks.Where(b => b.Chapter == ed.Chapter).Select(b => b.Block));
        if (save && _book != null)
        {
            var list = _bookmarkStore.For(_book.FilePath);
            list.Clear();
            list.AddRange(_bookmarks.Select(b => b.Data));
            _bookmarkStore.Save();
        }
    }

    private void OnToggleBookmarkClick(object sender, RoutedEventArgs e) => ToggleBookmark();

    private void OnNextBookmarkClick(object sender, RoutedEventArgs e) => StepBookmark(+1);

    private void OnPrevBookmarkClick(object sender, RoutedEventArgs e) => StepBookmark(-1);

    private void ToggleBookmark()
    {
        if (_book == null || ActiveTab?.Editor is not { } ed || ed.Chapter.Blocks.Count == 0) return;
        var loc = ed.CurrentLocation;
        var existing = _bookmarks.FirstOrDefault(b => b.Chapter == ed.Chapter && b.Block == loc.Block);
        if (existing != null)
        {
            _bookmarks.Remove(existing);
            StatusText.Text = T.Get("Status_BookmarkRemoved");
        }
        else
        {
            var text = ed.Chapter.Blocks[loc.Block].Text;
            var data = new BookmarkData { ChapterPath = ed.Chapter.Path, Block = loc.Block, Snippet = text.Length > 110 ? text[..110] : text };
            _bookmarks.Add(new BookmarkItem(_book, ed.Chapter, data));
            StatusText.Text = T.Get("Status_BookmarkAdded");
        }
        RefreshBookmarks();
    }

    private void StepBookmark(int direction)
    {
        if (_bookmarks.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }
        var cur = ActiveTab?.Editor?.CurrentLocation ?? new TextLocation(Math.Max(0, _currentChapter), 0, 0);
        var ordered = _bookmarks.ToList();
        BookmarkItem target = direction > 0
            ? ordered.FirstOrDefault(b => (b.Chapter.Index, b.Block).CompareTo((cur.Chapter, cur.Block)) > 0) ?? ordered[0]
            : ordered.LastOrDefault(b => (b.Chapter.Index, b.Block).CompareTo((cur.Chapter, cur.Block)) < 0) ?? ordered[^1];
        JumpTo(target.Location);
        BookmarkList.SelectedItem = target;
    }

    private void OnBookmarkClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkList.SelectedItem is BookmarkItem b) JumpTo(b.Location);
    }

    private void OnBookmarkKeyDown(object sender, KeyEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkItem b) return;
        if (e.Key == Key.Enter)
        {
            JumpTo(b.Location);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _bookmarks.Remove(b);
            RefreshBookmarks();
            e.Handled = true;
        }
    }

    private void OnDeleteBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is BookmarkItem b)
        {
            _bookmarks.Remove(b);
            RefreshBookmarks();
        }
    }

    /// <summary>Keeps bookmarks on their paragraph when paragraphs are split or merged before them.</summary>
    private void ShiftBookmarks(Chapter c, int block, int delta)
    {
        bool changed = false;
        foreach (var b in _bookmarks.Where(b => b.Chapter == c))
        {
            if (delta > 0 && b.Data.Block >= block) { b.Data.Block += delta; changed = true; }
            else if (delta < 0 && b.Data.Block > block) { b.Data.Block = Math.Max(block, b.Data.Block + delta); changed = true; }
        }
        if (changed) RefreshBookmarks();
    }

    // ================================================================== back / forward

    /// <summary>Remembers where we are before a jump (Alt+← comes back here).</summary>
    private void RecordJump()
    {
        if (ActiveTab?.Editor is { } ed) _nav.Record(ed.CurrentLocation);
    }

    /// <summary>A navigation the user asked for: recorded in the history.</summary>
    private void JumpTo(TextLocation loc)
    {
        RecordJump();
        NavigateTo(loc);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnForwardClick(object sender, RoutedEventArgs e) => GoForward();

    private void GoBack()
    {
        if (ActiveTab?.Editor is not { } ed) return;
        if (_nav.Back(ed.CurrentLocation) is { } loc) NavigateTo(loc);
        else SystemSounds.Beep.Play();
    }

    private void GoForward()
    {
        if (ActiveTab?.Editor is not { } ed) return;
        if (_nav.Forward(ed.CurrentLocation) is { } loc) NavigateTo(loc);
        else SystemSounds.Beep.Play();
    }

    // ================================================================== keys

    /// <summary>Search, bookmark and history shortcuts. True if handled.</summary>
    private bool HandleSearchShortcut(Key key, ModifierKeys mods)
    {
        switch (mods, key)
        {
            case (ModifierKeys.Control, Key.F): OpenSearch(wholeBook: false, replace: false); return true;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.F): OpenSearch(wholeBook: true, replace: false); return true;
            case (ModifierKeys.Control, Key.H): OpenSearch(wholeBook: true, replace: true); return true;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.P): ShowFindPassage(); return true;
            case (ModifierKeys.Control, Key.J): SetBottomPanel(BottomPanel.Visibility != Visibility.Visible); return true;
            case (ModifierKeys.None, Key.F3): StepHit(+1); return true;
            case (ModifierKeys.Shift, Key.F3): StepHit(-1); return true;
            case (ModifierKeys.Control, Key.F2): ToggleBookmark(); return true;
            case (ModifierKeys.None, Key.F2): StepBookmark(+1); return true;
            case (ModifierKeys.Shift, Key.F2): StepBookmark(-1); return true;
            case (ModifierKeys.Alt, Key.Left): GoBack(); return true;
            case (ModifierKeys.Alt, Key.Right): GoForward(); return true;
            case (ModifierKeys.None, Key.Escape) when Search.IsKeyboardFocusWithin:
                ActiveTab?.Editor?.FocusEditor();
                return true;
        }
        return false;
    }
}
