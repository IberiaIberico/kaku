using System.IO;
using System.Windows;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.App.Views;
using Kaku.Core;
using Kaku.Core.Reference;

namespace Kaku.App;

/// <summary>The original (Japanese) book in the «Original» tab: link, alignment, two-way sync, manual pins.</summary>
public partial class MainWindow
{
    private readonly ReferenceStore _referenceStore = ReferenceStore.Load();
    private ReferenceBook? _reference;
    private bool _pinMode;
    private (Chapter Chapter, int Block)? _originalShown;

    private void InitReference()
    {
        OriginalPreview.EmptyText = T.Get("Original_NoCounterpart");
        OriginalPreview.BlockClicked += OnOriginalBlockClicked;
        OriginalPreview.ShortcutPressed += HandlePreviewShortcut;
        UpdateOriginalUi();
    }

    private bool OriginalVisible => _settings.ShowPreview && RightTabs.SelectedItem == OriginalTab;

    private void OnRightTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != RightTabs) return;
        if (OriginalVisible) SyncOriginal(force: true);
    }

    // ================================================================== linking

    /// <summary>After a book opens: load its linked original in the background.</summary>
    private void LoadReferenceFor(Book book)
    {
        _reference = null;
        _originalShown = null;
        OriginalPreview.SetBook(null);
        UpdateOriginalUi();
        var link = _referenceStore.Get(book.FilePath);
        if (link == null || !File.Exists(link.Reference)) return;
        _ = OpenReferenceAsync(book, link);
    }

    private async Task OpenReferenceAsync(Book book, ReferenceLink link)
    {
        ReferenceBook reference;
        try
        {
            reference = await Task.Run(() => ReferenceBook.Load(link.Reference));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, T.F("Error_Open", link.Reference, ex.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_book != book) return; // another book was opened meanwhile
        foreach (var (path, pins) in link.ToPins()) reference.Pins[path] = pins;
        _reference = reference;
        OriginalPreview.SetBook(reference.Original);
        UpdateOriginalUi();
        SyncOriginal(force: true);
    }

    private void OnOriginalMenuClick(object sender, RoutedEventArgs e) => ChooseOriginal();

    private void OnChooseOriginalClick(object sender, RoutedEventArgs e) => ChooseOriginal();

    private void ChooseOriginal()
    {
        if (_book == null) return;
        var dlg = new ReferenceDialog(this, _book, _reference?.Path, CandidateFolders(_book));
        if (dlg.ShowDialog() != true) return;
        if (dlg.Removed)
        {
            _referenceStore.Remove(_book.FilePath);
            _referenceStore.Save();
            _reference = null;
            OriginalPreview.SetBook(null);
            UpdateOriginalUi();
            return;
        }
        if (dlg.Chosen is not { } path) return;
        var link = _referenceStore.Set(_book.FilePath, path);
        _referenceStore.Save();
        SetPanels(_settings.ShowLeftPanel, preview: true);
        RightTabs.SelectedItem = OriginalTab;
        _ = OpenReferenceAsync(_book, link);
    }

    /// <summary>Where originals are likely to be: the book's folder, its parent and siblings (e.g. out → kobo_downloads), recent folders.</summary>
    private List<string> CandidateFolders(Book book)
    {
        var folders = new List<string>();
        void Add(string? d)
        {
            if (d != null && Directory.Exists(d) && !folders.Contains(d, StringComparer.OrdinalIgnoreCase)) folders.Add(d);
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(book.FilePath));
        Add(dir);
        var parent = dir == null ? null : Path.GetDirectoryName(dir);
        Add(parent);
        if (parent != null)
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(parent).Take(40)) Add(sub);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var r in _settings.RecentFiles) Add(Path.GetDirectoryName(r));
        foreach (var link in _settings.RecentFiles.Select(_referenceStore.Get).OfType<ReferenceLink>()) Add(Path.GetDirectoryName(link.Reference));
        return folders;
    }

    private void UpdateOriginalUi()
    {
        bool linked = _reference != null;
        OriginalEmpty.Visibility = linked || _book == null ? Visibility.Collapsed : Visibility.Visible;
        PinButton.IsEnabled = linked;
        OriginalInfo.Text = !linked ? (_book == null ? "" : T.Get("Original_NotLinked"))
            : _originalShown is { } s ? T.F("Original_Info", Path.GetFileName(_reference!.Path), s.Chapter.Index + 1, s.Block + 1)
            : Path.GetFileName(_reference!.Path);
        if (_pinMode) OriginalInfo.Text = T.Get("Original_PinMode");
    }

    // ================================================================== sync

    /// <summary>Shows the original paragraph that matches the cursor (when the «Original» tab is visible).</summary>
    private void SyncOriginal(bool force = false)
    {
        if (!OriginalVisible || _reference == null || _book == null || ActiveTab?.Editor is not { } ed) return;
        var original = _reference.ChapterFor(_book, ed.Chapter);
        if (original == null)
        {
            _originalShown = null;
            OriginalPreview.ShowMessage(T.Get("Original_NoCounterpart"));
            UpdateOriginalUi();
            return;
        }
        int block = _reference.OriginalBlock(_book, ed.Chapter, ed.CurrentLocation.Block);
        if (!force && _originalShown is { } s && s.Chapter == original && s.Block == block) return;
        _originalShown = (original, block);
        OriginalPreview.ShowChapter(original, Math.Max(0, block));
        UpdateOriginalUi();
    }

    private void OnOriginalBlockClicked(int originalBlock)
    {
        if (_reference == null || _book == null || _originalShown is not { } shown) return;
        if (_pinMode)
        {
            CompletePin(shown.Chapter, originalBlock);
            return;
        }
        var chapter = _reference.TranslationFor(_book, shown.Chapter);
        if (chapter == null) return;
        int block = _reference.TranslatedBlock(_book, chapter, originalBlock);
        if (block < 0) return;
        _originalShown = (shown.Chapter, originalBlock);
        var ed = OpenChapter(chapter.Index, pin: false, location: new TextLocation(chapter.Index, block, 0), focus: true);
        ed.GoTo(new TextLocation(chapter.Index, block, 0));
        UpdateOriginalUi();
    }

    // ================================================================== manual pairs

    private void OnPinClick(object sender, RoutedEventArgs e) => StartPin();

    private void StartPin()
    {
        if (_reference == null || ActiveTab?.Editor == null) return;
        _pinMode = !_pinMode;
        StatusText.Text = _pinMode ? T.Get("Original_PinMode") : "";
        UpdateOriginalUi();
    }

    private void CompletePin(Chapter originalChapter, int originalBlock)
    {
        _pinMode = false;
        if (_book == null || _reference == null || ActiveTab?.Editor is not { } ed) return;
        if (_reference.ChapterFor(_book, ed.Chapter) != originalChapter)
        {
            UpdateOriginalUi();
            return;
        }
        var link = _referenceStore.Set(_book.FilePath, _reference.Path);
        var block = ed.CurrentLocation.Block;
        if (!link.Pins.TryGetValue(ed.Chapter.Path, out var list)) link.Pins[ed.Chapter.Path] = list = new();
        list.RemoveAll(p => p.Length == 2 && (p[0] == block || p[1] == originalBlock));
        list.Add([block, originalBlock]);
        _referenceStore.Save();
        _reference.Pins[ed.Chapter.Path] = link.ToPins()[ed.Chapter.Path];
        _reference.Invalidate(ed.Chapter);
        StatusText.Text = T.F("Original_Pinned", block + 1, originalBlock + 1);
        SyncOriginal(force: true);
    }

    private void OnClearPinsClick(object sender, RoutedEventArgs e)
    {
        if (_book == null || _reference == null || ActiveTab?.Editor is not { } ed) return;
        if (_referenceStore.Get(_book.FilePath) is { } link && link.Pins.Remove(ed.Chapter.Path)) _referenceStore.Save();
        _reference.Pins.Remove(ed.Chapter.Path);
        _reference.Invalidate(ed.Chapter);
        SyncOriginal(force: true);
    }

    /// <summary>Pins follow renamed chapters; the alignment is recomputed after structural changes.</summary>
    private void OnStructureChangedForReference(IReadOnlyDictionary<string, string> renamed)
    {
        if (_book == null || _reference == null) return;
        if (renamed.Count > 0 && _referenceStore.Get(_book.FilePath) is { } link)
        {
            foreach (var (from, to) in renamed)
                if (link.Pins.Remove(from, out var pins)) link.Pins[to] = pins;
            _referenceStore.Save();
            _reference.Pins.Clear();
            foreach (var (p, pins) in link.ToPins()) _reference.Pins[p] = pins;
        }
        _reference.Invalidate();
        SyncOriginal(force: true);
    }

    private bool HandleReferenceShortcut(Key key, ModifierKeys mods)
    {
        if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.O)
        {
            if (_reference == null) ChooseOriginal();
            else
            {
                SetPanels(_settings.ShowLeftPanel, preview: true);
                RightTabs.SelectedItem = RightTabs.SelectedItem == OriginalTab ? PreviewTab : OriginalTab;
            }
            return true;
        }
        if (_pinMode && mods == ModifierKeys.None && key == Key.Escape)
        {
            _pinMode = false;
            StatusText.Text = "";
            UpdateOriginalUi();
            return true;
        }
        return false;
    }
}
