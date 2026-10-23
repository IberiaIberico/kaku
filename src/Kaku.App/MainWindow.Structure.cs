using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.App.ViewModels;
using Kaku.App.Views;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Structure;
using Microsoft.Win32;

namespace Kaku.App;

/// <summary>Book structure: pages, files, images, TOC, metadata, checks; structural undo.</summary>
public partial class MainWindow
{
    private const string GalleryPath = "kaku:imagenes";
    private BookStructure? _structure;
    private Point _dragStart;
    private ChapterItem? _dragItem;

    private static readonly string ImageFilter = "Imágenes|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.svg|Todos los archivos|*.*";

    private void InitStructure()
    {
        ChapterList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(ChapterList);
            _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
        };
        ChapterList.PreviewMouseMove += OnChapterDragMove;
        ChapterList.AllowDrop = true;
        ChapterList.DragOver += OnChapterDragOver;
        ChapterList.Drop += OnChapterDrop;
        ChapterList.DragLeave += (_, _) => ClearDropMarker();
    }

    // ================================================================== running operations

    /// <summary>Runs a structural operation, reports errors, then refreshes every view.</summary>
    private StructureChange? Structure(Func<BookStructure, StructureChange> op)
    {
        if (_book == null || _structure == null) return null;
        FlushAllEditors();
        StructureChange change;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            change = op(_structure);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.Xml.XmlException)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, ex.Message, "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        AfterStructureChange(change);
        StatusText.Text = T.F("Status_StructureDone", change.Label);
        return change;
    }

    private void AfterStructureChange(StructureChange change)
    {
        var book = _book!;
        // tabs: follow renames, close what no longer exists, refresh what changed
        foreach (var t in _tabs.ToList())
        {
            if (t.Path == GalleryPath) continue;
            if (change.Renamed.TryGetValue(t.Path, out var np)) t.Path = np;
            if (!book.Package.Exists(t.Path))
            {
                CloseTab(t);
                continue;
            }
            if (t.Editor is { } ed)
            {
                if (change.Reloaded.Contains(ed.Chapter)) ed.ReloadFromModel();
                t.BaseHeader = $"{ed.Chapter.Index + 1}. {ed.Chapter.Title}";
                ((FrameworkElement)t.Item.Header).ToolTip = t.Path;
            }
            else if (book.Package.Find(t.Path) is { } entry && !ReferenceEquals(entry.Data, t.Data))
            {
                t.View = CreateFileView(entry);
                t.Item.Content = t.View;
                t.Data = entry.Data;
                t.BaseHeader = EpubPath.FileName(t.Path);
            }
        }
        foreach (var c in change.Reloaded) HistoryFor(c).Clear(); // their text undo steps refer to the old file

        // bookmarks follow renames, split and merge; bookmarks of deleted pages go
        foreach (var b in _bookmarks.ToList())
        {
            if (change.Renamed.TryGetValue(b.Data.ChapterPath, out var np)) b.Data.ChapterPath = np;
            foreach (var m in change.Moves)
                if (b.Data.ChapterPath == m.FromPath && b.Data.Block >= m.FromBlock)
                {
                    b.Data.ChapterPath = m.ToPath;
                    b.Data.Block = b.Data.Block - m.FromBlock + m.ToBlockOffset;
                }
        }
        var data = _bookmarks.Select(b => b.Data).Where(d => book.ChapterByPath(d.ChapterPath) != null).ToList();
        _bookmarks.Clear();
        foreach (var d in data) _bookmarks.Add(new BookmarkItem(book, book.ChapterByPath(d.ChapterPath)!, d));
        RefreshBookmarks();

        // positions and indexes changed everywhere
        _nav.Clear();
        _passageFinder = null;
        _blockCounts.Clear();
        foreach (var c in book.Chapters) _blockCounts[c] = c.Blocks.Count;
        _syncingSelection = true;
        BuildChapterItems(book);
        _syncingSelection = false;
        FileTree.ItemsSource = FileTreeBuilder.Build(book);
        if (_tabs.FirstOrDefault(t => t.Path == GalleryPath)?.View is GalleryView g) g.Load(book);
        if (BottomPanel.Visibility == Visibility.Visible && Search.Query != null) Search.Run();
        RefreshNotesPanel();
        ScheduleChecks();
        OnStructureChangedForReference(change.Renamed);
        _recoveryDirty = true;

        // show the result
        if (change.Focus != null && book.ChapterByPath(change.Focus) is { } focus)
            OpenChapter(focus.Index, pin: true, location: new TextLocation(focus.Index, change.FocusBlock, 0), focus: true);
        else if (ActiveTab?.Editor is { } active)
        {
            SetCurrentChapter(active.Chapter.Index);
            Preview.Reload(active.Chapter, active.CurrentLocation.Block);
        }
        else if (_tabs.Count == 0 && book.Chapters.Count > 0)
            OpenChapter(Math.Min(Math.Max(0, _currentChapter), book.Chapters.Count - 1), pin: false, location: null, focus: true);
        UpdateDirtyState();
        foreach (var t in _tabs) t.SetDirty(t.Editor?.Chapter.IsModified ?? book.ModifiedFiles.Contains(t.Path));
        UpdateStatus();
        UpdateStructureMenus();
    }

    private void UpdateStructureMenus()
    {
        UndoStructureMenu.IsEnabled = _structure?.CanUndo == true;
        UndoStructureMenu.Header = _structure?.UndoLabel is { } l ? T.F("Menu_UndoStructureLabel", l) : T.Get("Menu_UndoStructure");
        RedoStructureMenu.IsEnabled = _structure?.CanRedo == true;
    }

    private void OnUndoStructureClick(object sender, RoutedEventArgs e) => UndoStructure();

    private void OnRedoStructureClick(object sender, RoutedEventArgs e) => RedoStructure();

    private void UndoStructure()
    {
        if (_structure == null) return;
        FlushAllEditors();
        if (_structure.Undo() is { } inv)
        {
            AfterStructureChange(inv with { Focus = null });
            StatusText.Text = T.F("Status_StructureUndone", inv.Label);
        }
    }

    private void RedoStructure()
    {
        if (_structure == null) return;
        FlushAllEditors();
        if (_structure.Redo() is { } ch)
        {
            AfterStructureChange(ch with { Focus = null });
            StatusText.Text = T.F("Status_StructureDone", ch.Label);
        }
    }

    // ================================================================== pages

    private Chapter? CurrentChapter =>
        _book == null ? null : ActiveTab?.Editor?.Chapter ?? (_currentChapter >= 0 && _currentChapter < _book.Chapters.Count ? _book.Chapters[_currentChapter] : null);

    /// <summary>Chapters selected in the browser (or the current one).</summary>
    private List<Chapter> SelectedChapters()
    {
        var sel = ChapterList.SelectedItems.OfType<ChapterItem>().Select(i => i.Chapter).OrderBy(c => c.Index).ToList();
        if (sel.Count == 0 && CurrentChapter is { } c) sel.Add(c);
        return sel;
    }

    private void OnAddPageClick(object sender, RoutedEventArgs e) => AddPage(import: false, near: CurrentChapter, before: false);

    private void OnImportPageClick(object sender, RoutedEventArgs e) => AddPage(import: true, near: CurrentChapter, before: false);

    private void AddPage(bool import, Chapter? near, bool before)
    {
        if (_book == null) return;
        var dlg = new AddPageDialog(this, near != null, import);
        if (before) dlg.BeforeRadio.IsChecked = true;
        if (dlg.ShowDialog() != true) return;
        int index = dlg.Position switch
        {
            PagePosition.BeforeCurrent when near != null => near.Index,
            PagePosition.AfterCurrent when near != null => GroupEnd(near) + 1,
            _ => _book.Chapters.Count,
        };
        Structure(s => s.AddPage(dlg.PageTitle, dlg.Body, index, dlg.AddToToc));
    }

    /// <summary>Index of the last part of a chapter's group (its continuation files and nested entries).</summary>
    private int GroupEnd(Chapter c)
    {
        int end = c.Index;
        while (end + 1 < _book!.Chapters.Count && IsInside(_book.Chapters[end + 1], c)) end++;
        return end;
    }

    private static bool IsInside(Chapter child, Chapter group)
    {
        for (var p = child.Parent; p != null; p = p.Parent)
            if (p == group) return true;
        return false;
    }

    private void OnDeletePagesClick(object sender, RoutedEventArgs e) => DeletePages(SelectedChapters());

    private void DeletePages(List<Chapter> chapters)
    {
        if (_book == null || _structure == null || chapters.Count == 0) return;
        if (chapters.Count >= _book.Chapters.Count)
        {
            MessageBox.Show(this, T.Get("Pages_CannotDeleteAll"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var paths = chapters.Select(c => c.Path).ToHashSet();
        var usages = LinkIndex.Build(_book).Where(l => paths.Contains(l.Target) && !paths.Contains(l.Source) && l.Source != _book.Opf.NavPath && l.Source != _book.Opf.NcxPath).ToList();
        var names = string.Join("\n", chapters.Take(12).Select(c => $"• {c.Index + 1}. {c.Title}  ({EpubPath.FileName(c.Path)}, {T.N0(c.WordCount)} pal.)"));
        var msg = T.F("Pages_ConfirmDelete", chapters.Count, names)
                  + (usages.Count > 0 ? "\n\n" + T.F("Pages_DeleteLinks", usages.Count, usages.Select(u => u.Source).Distinct().Count()) : "");
        if (MessageBox.Show(this, msg, "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Structure(s => s.DeleteFiles(paths));
    }

    private void OnDuplicatePageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentChapter is { } c) Structure(s => s.Duplicate(c.Path));
    }

    private void OnRenamePageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentChapter is { } c) RenameFile(c.Path);
    }

    private void RenameFile(string path)
    {
        var name = InputDialog.Ask(this, T.Get("Pages_RenameTitle"), T.F("Pages_RenamePrompt", path), EpubPath.FileName(path));
        if (name != null && name != EpubPath.FileName(path)) Structure(s => s.Rename(path, name));
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveCurrent(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveCurrent(+1);

    /// <summary>The chapter and its parts, in reading order.</summary>
    private List<string> GroupPaths(Chapter c) =>
        _book!.Chapters.Skip(c.Index).Take(GroupEnd(c) - c.Index + 1).Select(x => x.Path).ToList();

    /// <summary>Moves the current chapter (with its parts) past the previous/next chapter at the same level.</summary>
    private void MoveCurrent(int delta)
    {
        if (_book == null || CurrentChapter is not { } c) return;
        var paths = GroupPaths(c);
        string? before;
        if (delta < 0)
        {
            var prev = _book.Chapters.Take(c.Index).LastOrDefault(x => x.Parent == c.Parent);
            if (prev == null) return;
            before = prev.Path;
        }
        else
        {
            int end = GroupEnd(c);
            if (end + 1 >= _book.Chapters.Count) return;
            var next = _book.Chapters[end + 1];
            if (next.Parent != c.Parent && !IsInside(next, c.Parent ?? next)) return;
            int nextEnd = GroupEnd(next);
            before = nextEnd + 1 < _book.Chapters.Count ? _book.Chapters[nextEnd + 1].Path : null;
        }
        Structure(s => s.Move(paths, before));
        if (_book.ChapterByPath(paths[0]) is { } moved) OpenChapter(moved.Index, pin: true, location: null, focus: false);
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Editor is not { } ed)
        {
            MessageBox.Show(this, T.Get("Pages_SplitNeedsEditor"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var loc = ed.CurrentLocation;
        Structure(s => s.Split(ed.Chapter, loc.Block));
    }

    private void OnMergeClick(object sender, RoutedEventArgs e)
    {
        if (_book == null || CurrentChapter is not { } c) return;
        if (c.Index + 1 >= _book.Chapters.Count) return;
        var next = _book.Chapters[c.Index + 1];
        if (MessageBox.Show(this, T.F("Pages_ConfirmMerge", c.Index + 1, c.Title, next.Index + 1, next.Title), "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Structure(s => s.MergeWithNext(c));
    }

    // ================================================================== chapter browser: context menu, drag & drop

    private void OnChapterContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_book == null)
        {
            e.Handled = true;
            return;
        }
        var item = ItemUnder(e.OriginalSource as DependencyObject);
        if (item != null && !ChapterList.SelectedItems.Contains(item))
        {
            ChapterList.SelectedItem = item;
        }
        var chapters = SelectedChapters();
        var c = chapters.FirstOrDefault();
        var menu = ChapterList.ContextMenu!;
        menu.Items.Clear();
        void Add(string key, Action action, bool enabled = true, string? gesture = null)
        {
            var mi = new MenuItem { Header = T.Get(key), IsEnabled = enabled, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        bool one = chapters.Count == 1 && c != null;
        Add("Ctx_Open", () => OpenChapter(c!.Index, pin: true, location: null, focus: true), one);
        menu.Items.Add(new Separator());
        Add("Ctx_AddBefore", () => AddPage(false, c, before: true), one);
        Add("Ctx_AddAfter", () => AddPage(false, c, before: false), one);
        Add("Ctx_Import", () => AddPage(true, c, before: false), one);
        menu.Items.Add(new Separator());
        Add("Ctx_Rename", () => RenameFile(c!.Path), one);
        Add("Ctx_Duplicate", () => Structure(s => s.Duplicate(c!.Path)), one);
        Add("Ctx_MoveUp", () => { OpenChapter(c!.Index, true, null, false); MoveCurrent(-1); }, one && c!.Index > 0, "Alt+↑");
        Add("Ctx_MoveDown", () => { OpenChapter(c!.Index, true, null, false); MoveCurrent(+1); }, one && c!.Index + 1 < _book.Chapters.Count, "Alt+↓");
        Add("Ctx_Merge", () => { OpenChapter(c!.Index, true, null, false); OnMergeClick(this, new RoutedEventArgs()); }, one && c!.Index + 1 < _book.Chapters.Count);
        Add("Ctx_Split", () => OnSplitClick(this, new RoutedEventArgs()), one && ActiveTab?.Editor?.Chapter == c);
        menu.Items.Add(new Separator());
        Add(chapters.Count > 1 ? "Ctx_DeleteMany" : "Ctx_Delete", () => DeletePages(chapters), chapters.Count > 0, "Supr");
        if (chapters.Count > 1) ((MenuItem)menu.Items[^1]).Header = T.F("Ctx_DeleteMany", chapters.Count);
    }

    private ChapterItem? ItemUnder(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as ChapterItem;
    }

    private ListBoxItem? ContainerUnder(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void OnChapterDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _book == null) return;
        var d = e.GetPosition(ChapterList) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2 && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton or System.Windows.Controls.Primitives.ScrollBar) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(ChapterList, new DataObject(typeof(ChapterItem), item), DragDropEffects.Move);
        ClearDropMarker();
    }

    private ListBoxItem? _dropMarker;
    private bool _dropAfter;

    private void OnChapterDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ChapterItem)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        var container = ContainerUnder(e.OriginalSource as DependencyObject);
        ClearDropMarker();
        if (container != null)
        {
            _dropAfter = e.GetPosition(container).Y > container.ActualHeight / 2;
            container.BorderBrush = Theme.Brush("DropMarker");
            container.BorderThickness = _dropAfter ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
            _dropMarker = container;
        }
        e.Handled = true;
    }

    private void ClearDropMarker()
    {
        if (_dropMarker == null) return;
        _dropMarker.BorderThickness = new Thickness(0);
        _dropMarker = null;
    }

    private void OnChapterDrop(object sender, DragEventArgs e)
    {
        var moving = e.Data.GetData(typeof(ChapterItem)) as ChapterItem;
        var target = ContainerUnder(e.OriginalSource as DependencyObject)?.DataContext as ChapterItem;
        bool after = _dropAfter;
        ClearDropMarker();
        if (_book == null || moving == null || target == null || moving == target) return;
        string? before;
        if (!after) before = target.Chapter.Path;
        else before = target.Chapter.Index + 1 < _book.Chapters.Count ? _book.Chapters[target.Chapter.Index + 1].Path : null;
        // dropping "after" a group parent means after all of its parts
        if (after && target.HasChildren && !target.IsExpanded)
        {
            int end = GroupEnd(target.Chapter);
            before = end + 1 < _book.Chapters.Count ? _book.Chapters[end + 1].Path : null;
        }
        var paths = GroupPaths(moving.Chapter);
        if (before != null && paths.Contains(before)) return;
        Structure(s => s.Move(paths, before));
    }

    private bool HandleChapterListKey(KeyEventArgs e)
    {
        if (!ChapterList.IsKeyboardFocusWithin) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            DeletePages(SelectedChapters());
            return true;
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down && ChapterList.SelectedItem is ChapterItem item)
        {
            OpenChapter(item.Chapter.Index, pin: true, location: null, focus: false);
            MoveCurrent(key == Key.Up ? -1 : +1);
            if (_book?.ChapterByPath(item.Chapter.Path) is { } moved) ChapterList.SelectedItem = _chapterItems[moved.Index];
            ChapterList.Focus();
            return true;
        }
        return false;
    }

    // ================================================================== files tab

    private void OnFileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_book == null)
        {
            e.Handled = true;
            return;
        }
        var file = FileTree.SelectedItem as FileItem;
        var menu = FileTree.ContextMenu!;
        menu.Items.Clear();
        void Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        bool isFile = file != null;
        bool isStructural = isFile && (file!.Path == _book.Opf.Path || file.Path == _book.Opf.NavPath || file.Path == _book.Opf.NcxPath
                                       || file.Path.StartsWith("META-INF/") || file.Path == EpubPackage.MimetypePath);
        bool isImage = isFile && _book.Opf.ItemByPath(file!.Path)?.IsImage == true;
        Add(T.Get("Ctx_Open"), () => OpenFileByPath(file!.Path, pin: true), isFile);
        Add(T.Get("Ctx_Rename"), () => RenameFile(file!.Path), isFile && !isStructural);
        Add(T.Get("Files_Replace"), () => ReplaceFileInteractive(file!.Path), isFile && !isStructural);
        if (isImage) Add(T.Get("Gallery_Cover"), () => Structure(s => s.SetCover(file!.Path)));
        Add(T.Get("Files_Usages"), () => ShowUsages(file!.Path), isFile);
        Add(T.Get("Ctx_Delete"), () => DeleteFileInteractive(file!.Path), isFile && !isStructural);
        menu.Items.Add(new Separator());
        Add(T.Get("Files_Add"), () => AddFilesInteractive(null));
        Add(T.Get("Menu_Gallery").Replace("_", ""), ShowGallery);
    }

    private void OnFileTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem tvi) tvi.IsSelected = true;
    }

    private void AddFilesInteractive(string? filter)
    {
        if (_book == null) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = T.Get("Files_Add"), Filter = filter ?? T.Get("Files_AddFilter") };
        if (dlg.ShowDialog(this) != true) return;
        StructureChange? last = null;
        foreach (var f in dlg.FileNames)
        {
            var bytes = File.ReadAllBytes(f);
            last = Structure(s => s.AddFile(Path.GetFileName(f), bytes)) ?? last;
        }
        if (last?.Focus is { } p && _tabs.FirstOrDefault(t => t.Path == GalleryPath)?.View is GalleryView g) g.Load(_book, p);
    }

    private void ReplaceFileInteractive(string path)
    {
        var ext = EpubPath.Extension(path);
        var dlg = new OpenFileDialog
        {
            Title = T.F("Files_ReplaceTitle", EpubPath.FileName(path)),
            Filter = _book!.Opf.ItemByPath(path)?.IsImage == true ? ImageFilter : $"*{ext}|*{ext}|Todos los archivos|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var bytes = File.ReadAllBytes(dlg.FileName);
        Structure(s => s.ReplaceFile(path, Path.GetFileName(dlg.FileName), bytes));
    }

    private void DeleteFileInteractive(string path)
    {
        if (_book == null || _structure == null) return;
        if (_book.ChapterByPath(path) is { } c)
        {
            DeletePages([c]);
            return;
        }
        var usages = _structure.UsagesOf(path);
        var msg = T.F("Files_ConfirmDelete", path) + (usages.Count > 0 ? "\n\n" + T.F("Files_DeleteUsed", usages.Count, string.Join("\n", usages.Select(u => u.Source).Distinct().Take(10))) : "");
        if (MessageBox.Show(this, msg, "Kaku", MessageBoxButton.OKCancel, usages.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Structure(s => s.DeleteFiles([path]));
    }

    private void ShowUsages(string path)
    {
        if (_structure == null) return;
        var usages = _structure.UsagesOf(path);
        if (usages.Count == 0)
        {
            MessageBox.Show(this, T.F("Files_NoUsages", path), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var u in usages)
        {
            var c = _book!.ChapterByPath(u.Source);
            var mi = new MenuItem { Header = (c != null ? $"{c.Index + 1}. {c.Title}" : u.Source) + " · " + T.F("Search_PositionLine", u.Line) };
            mi.Click += (_, _) => JumpToSource(u.Source, u.Line);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>Opens a file at a source line (chapters in code mode).</summary>
    private void JumpToSource(string path, int line)
    {
        if (_book == null) return;
        RecordJump();
        if (_book.ChapterByPath(path) is { } c)
        {
            var ed = OpenChapter(c.Index, pin: true, location: null, focus: true);
            ed.GoToSourceLine(Math.Max(1, line));
            return;
        }
        OpenFileByPath(path, pin: true);
        if (ActiveTab?.View is ICSharpCode.AvalonEdit.TextEditor code && line > 0 && line <= code.Document.LineCount)
        {
            code.ScrollTo(line, 1);
            code.TextArea.Caret.Line = line;
            code.Focus();
        }
    }

    // ================================================================== images

    private void OnGalleryClick(object sender, RoutedEventArgs e) => ShowGallery();

    private void OnAddFileClick(object sender, RoutedEventArgs e) => AddFilesInteractive(null);

    private void ShowGallery()
    {
        if (_book == null) return;
        var tab = OpenTab(GalleryPath, () =>
        {
            var g = new GalleryView();
            g.ActionRequested += OnGalleryAction;
            g.UsageRequested += JumpToSource;
            return g;
        }, T.Get("Gallery_TabTitle"), pin: true);
        ((GalleryView)tab.View).Load(_book);
    }

    private void OnGalleryAction(GalleryAction action, GalleryItem? item)
    {
        if (_book == null) return;
        switch (action)
        {
            case GalleryAction.Add:
                AddFilesInteractive(ImageFilter);
                break;
            case GalleryAction.Replace:
                ReplaceFileInteractive(item!.Path);
                break;
            case GalleryAction.Cover:
                Structure(s => s.SetCover(item!.Path));
                break;
            case GalleryAction.Delete:
                DeleteFileInteractive(item!.Path);
                break;
            case GalleryAction.Insert:
                InsertImage(item!.Path);
                break;
        }
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        if (_book == null) return;
        if (LastChapterEditor() == null)
        {
            MessageBox.Show(this, T.Get("Images_NeedEditor"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new Window
        {
            Title = T.Get("Images_PickTitle"), Owner = this, Width = 760, Height = 560, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var g = new GalleryView { PickerMode = true };
        string? chosen = null;
        g.ActionRequested += (a, item) =>
        {
            if (a == GalleryAction.Add)
            {
                AddFilesInteractive(ImageFilter);
                g.Load(_book);
            }
            else if (a == GalleryAction.Insert && item != null)
            {
                chosen = item.Path;
                picker.DialogResult = true;
            }
        };
        picker.Content = g;
        g.Load(_book);
        if (picker.ShowDialog() == true && chosen != null) InsertImage(chosen);
    }

    /// <summary>The chapter editor to insert into: the active tab, or the last chapter tab used.</summary>
    private Editor.ChapterEditorView? LastChapterEditor() =>
        ActiveTab?.Editor ?? _tabs.Select(t => t.Editor).LastOrDefault(e => e != null);

    private void InsertImage(string imagePath)
    {
        if (LastChapterEditor() is not { } ed)
        {
            MessageBox.Show(this, T.Get("Images_NeedEditor"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var tab = _tabs.First(t => t.Editor == ed);
        DocTabs.SelectedItem = tab.Item;
        if (!ed.InsertImageParagraph(imagePath)) System.Media.SystemSounds.Beep.Play();
    }

    // ================================================================== TOC, metadata, check

    private void OnTocClick(object sender, RoutedEventArgs e)
    {
        if (_book == null || _structure == null) return;
        FlushAllEditors();
        var dlg = new TocEditorDialog(this, _book, _structure, ActiveTab?.Editor?.CurrentLocation);
        if (dlg.ShowDialog() == true) Structure(s => s.SetToc(dlg.Result));
    }

    private void OnMetadataClick(object sender, RoutedEventArgs e)
    {
        if (_book == null || _structure == null) return;
        var dlg = new MetadataDialog(this, _book, _structure.GetMetadata());
        if (dlg.ShowDialog() != true) return;
        var cover = dlg.NewCover;
        Structure(s => s.SetMetadata(dlg.Result));
        if (cover != null) Structure(s => s.SetCover(cover));
        UpdateDirtyState();
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (_book == null) return;
        FlushAllEditors();
        var dlg = new BookCheckDialog(this, _book);
        dlg.FixRequested += (kind, paths) => Structure(s => kind switch
        {
            ProblemKind.Unused => s.DeleteFiles(paths),
            ProblemKind.NotInManifest => s.AddToManifest(paths),
            _ => s.RemoveFromManifest(paths),
        });
        dlg.JumpRequested += (path, line) =>
        {
            if (_book.Package.Exists(path)) JumpToSource(path, line);
        };
        dlg.Owner = this;
        dlg.Show();
    }

    private bool StructureFocus =>
        ChapterList.IsKeyboardFocusWithin || FileTree.IsKeyboardFocusWithin || NotesView.IsKeyboardFocusWithin
        || ActiveTab?.View is GalleryView { IsKeyboardFocusWithin: true };

    /// <summary>Book menu / structure shortcuts. True if handled.</summary>
    private bool HandleStructureShortcut(Key key, ModifierKeys mods)
    {
        switch (mods, key)
        {
            // Ctrl+Z / Ctrl+Y act on the book structure while a structure panel has the focus
            case (ModifierKeys.Control, Key.Z) when StructureFocus: UndoStructure(); return true;
            case (ModifierKeys.Control, Key.Y) when StructureFocus: RedoStructure(); return true;
            case (ModifierKeys.Control | ModifierKeys.Alt, Key.Enter): OnSplitClick(this, new RoutedEventArgs()); return true;
            case (ModifierKeys.Control | ModifierKeys.Alt, Key.I): OnInsertImageClick(this, new RoutedEventArgs()); return true;
            case (ModifierKeys.Control | ModifierKeys.Alt, Key.T): OnTocClick(this, new RoutedEventArgs()); return true;
        }
        return false;
    }
}
