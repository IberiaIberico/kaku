using System.ComponentModel;
using System.IO;
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
using Kaku.Core.Text;
using Microsoft.Win32;

namespace Kaku.App;

/// <summary>An open tab in the editor area.</summary>
internal sealed class DocTab
{
    public required string Path { get; set; }
    /// <summary>File content shown by a non-chapter view (to notice when the file changes).</summary>
    public byte[]? Data { get; set; }
    public required TabItem Item { get; init; }
    public required TextBlock HeaderText { get; init; }
    public FrameworkElement View { get; set; } = null!;
    public ChapterEditorView? Editor => View as ChapterEditorView;
    public bool IsPreview { get; private set; }
    public string BaseHeader { get; set; } = "";

    public void SetPreview(bool preview)
    {
        IsPreview = preview;
        HeaderText.FontStyle = preview ? FontStyles.Italic : FontStyles.Normal;
    }

    public void SetDirty(bool dirty) => HeaderText.Text = dirty ? BaseHeader + " ●" : BaseHeader;
}

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<DocTab> _tabs = new();
    private readonly List<ChapterItem> _chapterItems = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private Book? _book;
    private ICollectionView? _chapterView;
    private bool _syncingSelection;
    private bool _refreshQueued;
    private bool _previewFollowsItself;
    private int _lastPreviewBlock = -1;
    private int _currentChapter = -1;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowSettings();
        RebuildRecentMenu();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += (_, e) =>
        {
            if (!ConfirmDiscardChanges())
            {
                e.Cancel = true;
                return;
            }
            SaveWindowSettings();
        };
        InitEditing();

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            SyncPreviewScroll();
        };
        Preview.BlockClicked += OnPreviewBlockClicked;
        Preview.ChapterLoaded += OnPreviewChapterLoaded;
        Preview.ShortcutPressed += HandlePreviewShortcut;
        InitSearch();
        InitStructure();
        InitNotes();
        InitProblems();
        InitReference();
        if (_settings.ShowBottomPanel) Loaded += (_, _) => SetBottomPanel(true);
        UpdateStatus();
    }

    private DocTab? ActiveTab => DocTabs.SelectedItem is TabItem ti ? _tabs.FirstOrDefault(t => t.Item == ti) : null;

    // ================================================================== opening / closing books

    public async Task OpenBookAsync(string path, bool recover = false)
    {
        if (!ConfirmDiscardChanges()) return;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, T.F("Error_FileNotFound", path), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            _settings.RecentFiles.Remove(path);
            RebuildRecentMenu();
            return;
        }

        // unsaved work from a session that ended badly?
        var recovery = Kaku.Core.Editing.Recovery.Find(RecoveryRoot, path);
        if (recovery != null && !recover && MessageBox.Show(this, T.F("Recovery_Offer", recovery.SavedAt.ToString("g", T.Es), recovery.ChangedFiles), "Kaku",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            DeleteRecovery(path);
            recovery = null;
        }

        StatusText.Text = T.F("Status_Opening", Path.GetFileName(path));
        Mouse.OverrideCursor = Cursors.Wait;
        Book book;
        try
        {
            var r = recovery;
            book = await Task.Run(() => r != null ? Kaku.Core.Editing.Recovery.Open(RecoveryRoot, r) : Book.Load(path));
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, T.F("Error_Open", path, ex.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus();
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        try
        {
            ShowBook(path, book);
            if (recovery != null) AfterRecovered(book);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, T.F("Error_Open", path, ex.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowBook(string path, Book book)
    {
        CloseBook();
        _book = book;
        _bookStamp = Stamp(path);
        Title = $"{book.Title} — Kaku";
        _settings.AddRecent(path);
        _settings.Save();
        RebuildRecentMenu();
        CloseBookMenu.IsEnabled = true;
        SaveMenu.IsEnabled = SaveAsMenu.IsEnabled = true;

        ChapterFilter.Text = "";
        BuildChapterItems(book);
        FileTree.ItemsSource = FileTreeBuilder.Build(book);
        Preview.SetBook(book);
        ResetSearchState(book);
        _structure = new Kaku.Core.Structure.BookStructure(book);
        LearnBookNames(book);
        LoadReferenceFor(book);
        UpdateStructureMenus();

        // open the first chapter with real text (skips cover / image pages)
        var first = book.Chapters.FirstOrDefault(c => c.WordCount > 50) ?? book.Chapters.FirstOrDefault();
        if (first != null) OpenChapter(first.Index, pin: false, location: null, focus: true);
        UpdateStatus();
    }

    /// <summary>(Re)creates the chapter browser rows; expanded groups stay expanded.</summary>
    private void BuildChapterItems(Book book)
    {
        var expanded = _chapterItems.Where(i => i.IsExpanded).Select(i => i.Chapter).ToHashSet();
        foreach (var old in _chapterItems) old.PropertyChanged -= OnChapterItemChanged;
        _chapterItems.Clear();
        foreach (var c in book.Chapters) _chapterItems.Add(new ChapterItem(book, c) { IsExpanded = expanded.Contains(c) });
        foreach (var item in _chapterItems)
        {
            if (item.Chapter.Parent is { } parent)
            {
                item.Parent = _chapterItems[parent.Index];
                item.Parent.Children.Add(item);
            }
            item.PropertyChanged += OnChapterItemChanged;
        }
        _chapterView = System.Windows.Data.CollectionViewSource.GetDefaultView(_chapterItems);
        ApplyChapterFilter();
        ChapterList.ItemsSource = null;
        ChapterList.ItemsSource = _chapterView;
    }

    private void CloseBook()
    {
        foreach (var t in _tabs.ToList()) CloseTab(t);
        _book = null;
        _chapterItems.Clear();
        ChapterList.ItemsSource = null;
        _chapterView = null;
        FileTree.ItemsSource = null;
        Preview.SetBook(null);
        ResetSearchState(null);
        _structure = null;
        UpdateStructureMenus();
        _reference = null;
        OriginalPreview.SetBook(null);
        UpdateOriginalUi();
        _currentChapter = -1;
        Title = "Kaku";
        CloseBookMenu.IsEnabled = false;
        SaveMenu.IsEnabled = SaveAsMenu.IsEnabled = false;
        UpdateStatus();
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await ShowOpenDialogAsync();

    private async Task ShowOpenDialogAsync()
    {
        var dlg = new OpenFileDialog { Filter = T.Get("Dialog_EpubFilter"), Title = T.Get("Dialog_OpenTitle") };
        if (_settings.RecentFiles.FirstOrDefault() is { } last && Path.GetDirectoryName(last) is { } dir && Directory.Exists(dir))
            dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) await OpenBookAsync(dlg.FileName);
    }

    private void OnCloseBookClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardChanges()) CloseBook();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = Path.GetFileName(path).Replace("_", "__"), ToolTip = path };
            item.Click += async (_, _) => await OpenBookAsync(path);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuItem { Header = T.Get("Menu_RecentEmpty"), IsEnabled = false });
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.FirstOrDefault(f => f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) is { } epub)
            await OpenBookAsync(epub);
    }

    // ================================================================== tabs

    private DocTab OpenTab(string path, Func<FrameworkElement> createView, string header, bool pin)
    {
        var existing = _tabs.FirstOrDefault(t => t.Path == path);
        if (existing != null)
        {
            if (pin) existing.SetPreview(false);
            DocTabs.SelectedItem = existing.Item;
            return existing;
        }

        var reuse = pin ? null : _tabs.FirstOrDefault(t => t.IsPreview);
        if (reuse != null) _tabs.Remove(reuse);

        var headerText = new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = "×", Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent, Focusable = false, ToolTip = T.Get("Tab_Close"),
        };
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = path };
        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(close);

        var item = reuse?.Item ?? new TabItem();
        item.Header = headerPanel;
        var tab = new DocTab { Path = path, Item = item, HeaderText = headerText, View = createView(), BaseHeader = header };
        item.Content = tab.View;
        tab.SetPreview(!pin);
        close.Click += (_, _) => CloseTab(tab);
        headerPanel.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(tab);
            else if (e.ClickCount == 2) tab.SetPreview(false);
        };

        if (reuse == null) DocTabs.Items.Add(item);
        _tabs.Add(tab);
        DocTabs.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        if (DocTabs.SelectedItem == item) OnActiveTabChanged();
        else DocTabs.SelectedItem = item;
        return tab;
    }

    private void CloseTab(DocTab tab)
    {
        tab.Editor?.FlushCode(); // edits stay in the book model; only the view goes away
        _tabs.Remove(tab);
        DocTabs.Items.Remove(tab.Item);
        if (_tabs.Count == 0)
        {
            DocTabs.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            OnActiveTabChanged();
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } t) CloseTab(t);
    }

    private void OnDocTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == DocTabs) OnActiveTabChanged();
    }

    private void OnActiveTabChanged()
    {
        var tab = ActiveTab;
        if (tab?.Editor is { } ed)
        {
            SetCurrentChapter(ed.Chapter.Index);
            if (_previewFollowsItself) Preview.SetCurrentPath(ed.Chapter.Path);
            else Preview.ShowChapter(ed.Chapter, ed.CurrentLocation.Block);
            _lastPreviewBlock = ed.CurrentLocation.Block;
            SyncOriginal(force: true);
        }
        UpdateStatus();
    }

    // ================================================================== chapters & navigation

    private ChapterEditorView OpenChapter(int index, bool pin, TextLocation? location, bool focus)
    {
        var book = _book!;
        var chapter = book.Chapters[Math.Clamp(index, 0, book.Chapters.Count - 1)];
        var tab = OpenTab(chapter.Path, () =>
        {
            var view = new ChapterEditorView(book, chapter, HistoryFor(chapter));
            view.CaretMoved += OnEditorCaretMoved;
            view.Edited += OnChapterEdited;
            view.NoteText = NoteTextFor;
            view.NoteMarkerActivated += OnNoteMarkerActivated;
            view.SpellingEnabled = _settings.SpellCheck;
            view.InsertNoteRequested = InsertNote;
            view.SetBookmarks(_bookmarks.Where(b => b.Chapter == chapter).Select(b => b.Block));
            if (BottomPanel.Visibility == Visibility.Visible) view.SetSearchHits(Search.Hits);
            _blockCounts[chapter] = chapter.Blocks.Count;
            return view;
        }, $"{chapter.Index + 1}. {chapter.Title}", pin);

        var editor = tab.Editor!;
        if (location is { } loc) editor.GoTo(loc);
        if (focus) Dispatcher.BeginInvoke(editor.FocusEditor, DispatcherPriority.Input);
        return editor;
    }

    /// <summary>Opens the chapter of the location, puts the caret there and syncs preview + browser.</summary>
    private void NavigateTo(TextLocation loc, bool focus = true)
    {
        if (_book == null || _book.Chapters.Count == 0) return;
        OpenChapter(loc.Chapter, pin: false, location: loc, focus: focus);
    }

    private void SetCurrentChapter(int index)
    {
        _currentChapter = index;
        foreach (var item in _chapterItems) item.IsCurrent = item.Chapter.Index == index;
        if (index >= 0 && index < _chapterItems.Count)
        {
            // make the current chapter visible: open the groups it is nested in
            foreach (var a in _chapterItems[index].Ancestors) a.IsExpanded = true;
            UpdateContainsCurrent();
            _syncingSelection = true;
            ChapterList.SelectedItem = _chapterItems[index];
            ChapterList.ScrollIntoView(_chapterItems[index]);
            _syncingSelection = false;
        }
    }

    private void OnEditorCaretMoved(object? sender, EventArgs e)
    {
        if (sender != ActiveTab?.Editor) return;
        UpdateStatus();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void SyncPreviewScroll()
    {
        if (ActiveTab?.Editor is not { } ed) return;
        int block = ed.CurrentLocation.Block;
        SyncOriginal();
        if (block == _lastPreviewBlock) return;
        _lastPreviewBlock = block;
        Preview.ScrollToBlock(block);
    }

    private void OnPreviewBlockClicked(int block)
    {
        if (ActiveTab?.Editor is not { } ed) return;
        _lastPreviewBlock = block;
        ed.GoTo(new TextLocation(ed.Chapter.Index, block, 0));
    }

    private void OnPreviewChapterLoaded(string path)
    {
        if (_book?.ChapterByPath(path) is not { } chapter || ActiveTab?.Editor?.Chapter == chapter) return;
        _previewFollowsItself = true;
        try
        {
            OpenChapter(chapter.Index, pin: false, location: null, focus: false);
        }
        finally
        {
            _previewFollowsItself = false;
        }
    }

    private void StepChapter(int delta)
    {
        if (_book == null || _book.Chapters.Count == 0) return;
        int current = ActiveTab?.Editor?.Chapter.Index ?? _currentChapter;
        int next = Math.Clamp(current + delta, 0, _book.Chapters.Count - 1);
        if (next == current && ActiveTab?.Editor != null) return;
        RecordJump();
        OpenChapter(next, pin: false, location: null, focus: true);
    }

    private void OnNextChapterClick(object sender, RoutedEventArgs e) => StepChapter(+1);

    private void OnPrevChapterClick(object sender, RoutedEventArgs e) => StepChapter(-1);

    private void OnChapterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ChapterList.SelectedItems.Count != 1 || ChapterList.SelectedItem is not ChapterItem item) return;
        if (ActiveTab?.Editor?.Chapter != item.Chapter) RecordJump();
        OpenChapter(item.Chapter.Index, pin: false, location: null, focus: false);
    }

    private void OnChapterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChapterList.SelectedItem is ChapterItem item) OpenChapter(item.Chapter.Index, pin: true, location: null, focus: true);
    }

    private void OnChapterListKeyDown(object sender, KeyEventArgs e)
    {
        if (ChapterList.SelectedItem is not ChapterItem item) return;
        switch (e.Key)
        {
            case Key.Enter:
                OpenChapter(item.Chapter.Index, pin: true, location: null, focus: true);
                e.Handled = true;
                break;
            case Key.Right when item.CanExpand && !item.IsExpanded:
                item.IsExpanded = true;
                e.Handled = true;
                break;
            case Key.Left when item.IsExpanded:
                item.IsExpanded = false;
                e.Handled = true;
                break;
            case Key.Left when item.Parent != null:
                ChapterList.SelectedItem = item.Parent; // opens the parent chapter, like moving up in a tree
                ChapterList.ScrollIntoView(item.Parent);
                e.Handled = true;
                break;
        }
    }

    private void OnHeadingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HeadingItem h)
            JumpTo(new TextLocation(h.Owner.Chapter.Index, h.BlockIndex, 0));
    }

    private void OnChapterFilterChanged(object sender, TextChangedEventArgs e) => ApplyChapterFilter();

    /// <summary>No filter: tree view (children of collapsed chapters hidden). Filter: flat list of matches.</summary>
    private void ApplyChapterFilter()
    {
        if (_chapterView == null) return;
        var q = TextNormalizer.Fold(ChapterFilter.Text.Trim());
        _chapterView.Filter = q.Length == 0 ? o => ((ChapterItem)o).IsVisibleInTree : o => ((ChapterItem)o).Matches(q);
    }

    private void OnChapterItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChapterItem.IsExpanded) || sender is not ChapterItem { HasChildren: true }) return;
        if (_refreshQueued) return;
        // several groups may open at once (SetCurrentChapter); refresh the list once
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            _chapterView?.Refresh();
            UpdateContainsCurrent();
            if (_currentChapter >= 0 && _currentChapter < _chapterItems.Count && _chapterItems[_currentChapter].IsVisibleInTree)
            {
                _syncingSelection = true;
                ChapterList.SelectedItem = _chapterItems[_currentChapter];
                _syncingSelection = false;
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateContainsCurrent()
    {
        var current = _currentChapter >= 0 && _currentChapter < _chapterItems.Count ? _chapterItems[_currentChapter] : null;
        var hiddenIn = current?.Ancestors.LastOrDefault(a => !a.IsExpanded);
        foreach (var item in _chapterItems) item.ContainsCurrent = item == hiddenIn;
    }

    // ================================================================== files

    private void OpenFileByPath(string path, bool pin)
    {
        if (_book == null) return;
        var chapter = _book.ChapterByPath(path);
        if (chapter != null)
        {
            OpenChapter(chapter.Index, pin, null, focus: true);
            return;
        }
        var entry = _book.Package.Find(path);
        if (entry == null) return;
        var tab = OpenTab(entry.Path, () => CreateFileView(entry), Kaku.Core.Epub.EpubPath.FileName(entry.Path), pin);
        tab.Data ??= entry.Data;
    }

    private void OnFileTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is FileItem f)
        {
            OpenFileByPath(f.Path, pin: true);
            e.Handled = true;
        }
    }

    private void OnFileTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileTree.SelectedItem is FileItem f)
        {
            OpenFileByPath(f.Path, pin: true);
            e.Handled = true;
        }
    }

    // ================================================================== palette & go to

    private void OnPaletteClick(object sender, RoutedEventArgs e) => ShowPalette();

    private void OnGoToClick(object sender, RoutedEventArgs e) => ShowGoTo();

    private void ShowPalette()
    {
        var commands = new List<PaletteItem>
        {
            new(T.Get("Palette_Command"), T.Get("Cmd_Open"), "Ctrl+O", () => _ = ShowOpenDialogAsync()),
            new(T.Get("Palette_Command"), T.Get("Cmd_Save"), "Ctrl+S", () => _ = SaveAsync(saveAs: false)),
            new(T.Get("Palette_Command"), T.Get("Cmd_SaveAs"), "Ctrl+Mayús+S", () => _ = SaveAsync(saveAs: true)),
            new(T.Get("Palette_Command"), T.Get("Cmd_FindBook"), "Ctrl+Mayús+F", () => OpenSearch(wholeBook: true, replace: false)),
            new(T.Get("Palette_Command"), T.Get("Cmd_FindChapter"), "Ctrl+F", () => OpenSearch(wholeBook: false, replace: false)),
            new(T.Get("Palette_Command"), T.Get("Cmd_Replace"), "Ctrl+H", () => OpenSearch(wholeBook: true, replace: true)),
            new(T.Get("Palette_Command"), T.Get("Cmd_FindPassage"), "Ctrl+Mayús+P", ShowFindPassage),
            new(T.Get("Palette_Command"), T.Get("Cmd_ToggleBookmark"), "Ctrl+F2", ToggleBookmark),
            new(T.Get("Palette_Command"), T.Get("Cmd_Back"), "Alt+←", GoBack),
            new(T.Get("Palette_Command"), T.Get("Cmd_Forward"), "Alt+→", GoForward),
            new(T.Get("Palette_Command"), T.Get("Cmd_ToggleBottom"), "Ctrl+J", () => SetPanels(_settings.ShowLeftPanel, _settings.ShowPreview, bottomToggle: true)),
            new(T.Get("Palette_Command"), T.Get("Cmd_Undo"), "Ctrl+Z", Undo),
            new(T.Get("Palette_Command"), T.Get("Cmd_Redo"), "Ctrl+Y", Redo),
            new(T.Get("Palette_Command"), T.Get("Cmd_Bold"), "Ctrl+B", () => ActiveTab?.Editor?.ToggleFormat(SpanKind.Bold)),
            new(T.Get("Palette_Command"), T.Get("Cmd_Italic"), "Ctrl+I", () => ActiveTab?.Editor?.ToggleFormat(SpanKind.Italic)),
            new(T.Get("Palette_Command"), T.Get("Cmd_GoTo"), "Ctrl+G", ShowGoTo),
            new(T.Get("Palette_Command"), T.Get("Cmd_ToggleMode"), "Ctrl+E", ToggleMode),
            new(T.Get("Palette_Command"), T.Get("Cmd_NextChapter"), "Ctrl+AvPág", () => StepChapter(+1)),
            new(T.Get("Palette_Command"), T.Get("Cmd_PrevChapter"), "Ctrl+RePág", () => StepChapter(-1)),
            new(T.Get("Palette_Command"), T.Get("Cmd_ToggleLeft"), "Ctrl+Alt+B", () => SetPanels(!_settings.ShowLeftPanel, _settings.ShowPreview)),
            new(T.Get("Palette_Command"), T.Get("Cmd_TogglePreview"), "Ctrl+Alt+P", () => SetPanels(_settings.ShowLeftPanel, !_settings.ShowPreview)),
            new(T.Get("Palette_Command"), T.Get("Cmd_CloseTab"), "Ctrl+W", () => { if (ActiveTab is { } t) CloseTab(t); }),
        };
        var items = new List<PaletteItem>();
        var chapters = new List<PaletteItem>();
        if (_book != null)
        {
            foreach (var ci in _chapterItems)
            {
                var c = ci.Chapter;
                chapters.Add(new PaletteItem(T.Get("Palette_Chapter"), $"{c.Index + 1}. {c.Title}", ci.PositionText,
                    () => JumpTo(new TextLocation(c.Index, 0, 0))));
                foreach (var h in c.Headings)
                    items.Add(new PaletteItem(T.Get("Palette_Heading"), h.Title, $"{c.Index + 1}. {c.Title}",
                        () => JumpTo(new TextLocation(c.Index, h.BlockIndex, 0))));
            }
            foreach (var entry in _book.Package.Entries)
            {
                var path = entry.Path;
                items.Add(new PaletteItem(T.Get("Palette_File"), Kaku.Core.Epub.EpubPath.FileName(path), path,
                    () => OpenFileByPath(path, pin: true), path));
            }
        }
        items.InsertRange(0, chapters);
        items.AddRange(commands);
        new CommandPalette(this, items, chapters.Count > 0 ? chapters : commands).Show();
    }

    private void ShowGoTo()
    {
        if (_book == null || _book.Chapters.Count == 0) return;
        var dlg = new GoToDialog(this, _book);
        if (dlg.ShowDialog() == true && dlg.Target is { } target) JumpTo(target.Location);
    }

    // ================================================================== view

    private void OnToggleModeClick(object sender, RoutedEventArgs e) => ToggleMode();

    private void ToggleMode()
    {
        ActiveTab?.Editor?.ToggleMode();
        UpdateStatus();
    }

    private void OnPanelsMenuClick(object sender, RoutedEventArgs e) =>
        SetPanels(ShowLeftMenu.IsChecked, ShowPreviewMenu.IsChecked);

    private void SetPanels(bool left, bool preview, bool bottomToggle)
    {
        SetPanels(left, preview);
        if (bottomToggle) SetBottomPanel(BottomPanel.Visibility != Visibility.Visible);
    }

    private void SetPanels(bool left, bool preview)
    {
        if (_settings.ShowLeftPanel && LeftColumn.ActualWidth > 20) _settings.LeftPanelWidth = LeftColumn.ActualWidth;
        if (_settings.ShowPreview && RightColumn.ActualWidth > 20) _settings.RightPanelWidth = RightColumn.ActualWidth;
        _settings.ShowLeftPanel = left;
        _settings.ShowPreview = preview;
        ShowLeftMenu.IsChecked = left;
        ShowPreviewMenu.IsChecked = preview;
        LeftColumn.Width = new GridLength(left ? _settings.LeftPanelWidth : 0);
        LeftSplitterColumn.Width = new GridLength(left ? 4 : 0);
        LeftTabs.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = LeftTabs.Visibility;
        RightColumn.Width = new GridLength(preview ? _settings.RightPanelWidth : 0);
        RightSplitterColumn.Width = new GridLength(preview ? 4 : 0);
        RightPanel.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = RightPanel.Visibility;
    }

    private void UpdateStatus()
    {
        StatusDirty.Visibility = _book?.IsModified == true ? Visibility.Visible : Visibility.Collapsed;
        var tab = ActiveTab;
        if (_book == null)
        {
            StatusText.Text = T.Get("Status_NoBook");
            StatusMode.Text = "";
            return;
        }
        if (tab?.Editor is { } ed)
        {
            var loc = ed.CurrentLocation;
            var c = ed.Chapter;
            long w = _book.WordIndexAt(loc);
            double pct = _book.PercentAt(w);
            StatusText.Text = T.F("Status_Location", c.Index + 1, _book.Chapters.Count, c.Title,
                c.Blocks.Count == 0 ? 0 : loc.Block + 1, c.Blocks.Count,
                T.N0(Math.Min(w + 1, _book.TotalWords)), T.N0(_book.TotalWords), T.N1(pct));
            StatusMode.Text = T.Get(ed.IsCodeMode ? "Status_CodeMode" : "Status_TextMode");
            if (c.Index < _chapterItems.Count)
                _chapterItems[c.Index].CursorPercent = c.WordCount == 0 ? 0 : (w - c.WordStart) * 100.0 / c.WordCount;
        }
        else if (tab != null)
        {
            StatusText.Text = tab.Path == GalleryPath ? T.Get("Gallery_TabTitle") : tab.Path;
            StatusMode.Text = "";
        }
        else
        {
            StatusText.Text = T.F("Status_Book", _book.Title, _book.Chapters.Count, T.N0(_book.TotalWords));
            StatusMode.Text = "";
        }
    }

    // ================================================================== keyboard

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleChapterListKey(e) || HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>All window-level shortcuts (also used for keys pressed while the preview has focus).</summary>
    private bool HandleKey(Key key, ModifierKeys mods)
    {
        if (HandleReferenceShortcut(key, mods) || HandleStructureShortcut(key, mods) || HandleNotesShortcut(key, mods) || HandleProblemsShortcut(key, mods) || HandleEditShortcut(key, mods) || HandleSearchShortcut(key, mods)) return true;
        bool handled = true;
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.O: _ = ShowOpenDialogAsync(); break;
                case Key.P: ShowPalette(); break;
                case Key.G: ShowGoTo(); break;
                case Key.E: ToggleMode(); break;
                case Key.W: if (ActiveTab is { } t) CloseTab(t); break;
                case Key.PageDown: StepChapter(+1); break;
                case Key.PageUp: StepChapter(-1); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.P)
        {
            SetPanels(_settings.ShowLeftPanel, !_settings.ShowPreview);
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.B)
        {
            SetPanels(!_settings.ShowLeftPanel, _settings.ShowPreview);
        }
        else
        {
            handled = false;
        }
        return handled;
    }

    /// <summary>Keys forwarded by the preview's script (the WebView has its own keyboard focus).</summary>
    private void HandlePreviewShortcut(string key, bool shift, bool ctrl, bool alt)
    {
        var k = key switch
        {
            "PageDown" => Key.PageDown,
            "PageUp" => Key.PageUp,
            "ArrowLeft" => Key.Left,
            "ArrowRight" => Key.Right,
            "F2" => Key.F2,
            "F3" => Key.F3,
            { Length: 1 } c when char.IsLetter(c[0]) => (Key)Enum.Parse(typeof(Key), c.ToUpperInvariant()),
            _ => Key.None,
        };
        if (k == Key.None) return;
        var mods = (ctrl ? ModifierKeys.Control : 0) | (shift ? ModifierKeys.Shift : 0) | (alt ? ModifierKeys.Alt : 0);
        HandleKey(k, mods);
    }

    // ================================================================== settings

    private void RestoreWindowSettings()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top
            && left < SystemParameters.VirtualScreenWidth - 100 && top < SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        SetPanels(_settings.ShowLeftPanel, _settings.ShowPreview);
    }

    private void SaveWindowSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_settings.ShowLeftPanel && LeftColumn.ActualWidth > 20) _settings.LeftPanelWidth = LeftColumn.ActualWidth;
        if (_settings.ShowPreview && RightColumn.ActualWidth > 20) _settings.RightPanelWidth = RightColumn.ActualWidth;
        if (_settings.ShowBottomPanel && BottomRow.ActualHeight > 40) _settings.BottomPanelHeight = BottomRow.ActualHeight;
        _settings.Save();
    }
}
