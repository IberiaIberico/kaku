using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kaku.App.Editor;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Editing;
using Kaku.Core.Epub;
using Kaku.Core.Text;
using Microsoft.Win32;

namespace Kaku.App;

/// <summary>Editing side of the main window: change notifications, saving, backups and crash recovery.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(20);
    private readonly DispatcherTimer _previewEditTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = RecoveryInterval };
    private readonly Dictionary<int, string> _pendingBlockUpdates = new();
    private bool _pendingPreviewReload;
    private bool _recoveryDirty;
    private (long Length, DateTime WriteTime) _bookStamp;

    private void InitEditing()
    {
        _previewEditTimer.Tick += (_, _) =>
        {
            _previewEditTimer.Stop();
            FlushPreviewEdits();
        };
        _statsTimer.Tick += (_, _) =>
        {
            _statsTimer.Stop();
            RefreshBookStats();
        };
        _recoveryTimer.Tick += (_, _) => WriteRecovery();
        _recoveryTimer.Start();
    }

    // ================================================================== change notifications

    private void OnChapterEdited(ChapterEditorView view, EditKind kind, int block)
    {
        _recoveryDirty = true;
        OnChapterEditedForSearch(view, kind, block);
        ScheduleChecks();
        var tab = _tabs.FirstOrDefault(t => t.View == view);
        tab?.SetPreview(false); // an edited tab stays open (like VS Code)

        if (ActiveTab?.Editor == view)
        {
            if (kind == EditKind.Text && view.BlockInnerXhtml(block) is { } html) _pendingBlockUpdates[block] = html;
            else _pendingPreviewReload = true;
            _previewEditTimer.Stop();
            _previewEditTimer.Start();
        }
        _statsTimer.Stop();
        _statsTimer.Start();
        UpdateDirtyState();
        UpdateStatus();
    }

    private void FlushPreviewEdits()
    {
        if (ActiveTab?.Editor is not { } ed) return;
        if (_pendingPreviewReload)
            Preview.Reload(ed.Chapter, ed.CurrentLocation.Block);
        else
            foreach (var (block, html) in _pendingBlockUpdates) Preview.ReplaceBlock(block, html);
        _pendingPreviewReload = false;
        _pendingBlockUpdates.Clear();
        _lastPreviewBlock = ed.CurrentLocation.Block;
    }

    /// <summary>Titles, headings, positions and badges after edits (debounced).</summary>
    private void RefreshBookStats()
    {
        if (_book == null) return;
        _book.RefreshPositions();
        _book.RefreshTitles();
        foreach (var item in _chapterItems) item.RefreshState();
        RefreshNotesPanel();
        UpdateStatus();
    }

    private void UpdateDirtyState()
    {
        if (_book == null)
        {
            Title = "Kaku";
            return;
        }
        Title = (_book.IsModified ? "• " : "") + $"{_book.Title} — Kaku";
        foreach (var t in _tabs)
        {
            bool dirty = t.Editor?.Chapter.IsModified ?? _book.ModifiedFiles.Contains(t.Path);
            t.SetDirty(dirty);
        }
        SaveMenu.IsEnabled = true;
    }

    // ================================================================== edit commands (menu + shortcuts)

    private bool FocusInTextBox => Keyboard.FocusedElement is TextBox;

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void OnBoldClick(object sender, RoutedEventArgs e) => ActiveTab?.Editor?.ToggleFormat(SpanKind.Bold);

    private void OnItalicClick(object sender, RoutedEventArgs e) => ActiveTab?.Editor?.ToggleFormat(SpanKind.Italic);

    private void Undo()
    {
        if (ActiveTab?.Editor is { } ed) ed.Undo();
        else if (ActiveTab?.View is ICSharpCode.AvalonEdit.TextEditor code) code.Undo();
    }

    private void Redo()
    {
        if (ActiveTab?.Editor is { } ed) ed.Redo();
        else if (ActiveTab?.View is ICSharpCode.AvalonEdit.TextEditor code) code.Redo();
    }

    /// <summary>Shortcuts handled at window level so they work wherever the focus is. True if handled.</summary>
    private bool HandleEditShortcut(Key key, ModifierKeys mods)
    {
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.S: _ = SaveAsync(saveAs: false); return true;
                case Key.Z when !FocusInTextBox: Undo(); return true;
                case Key.Y when !FocusInTextBox: Redo(); return true;
                case Key.B when ActiveTab?.Editor != null: ActiveTab.Editor.ToggleFormat(SpanKind.Bold); return true;
                case Key.I when ActiveTab?.Editor != null: ActiveTab.Editor.ToggleFormat(SpanKind.Italic); return true;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.S: _ = SaveAsync(saveAs: true); return true;
                case Key.Z when !FocusInTextBox: Redo(); return true;
            }
        }
        return false;
    }

    // ================================================================== non-chapter files

    private FrameworkElement CreateFileView(EpubEntry entry)
    {
        bool structural = _book != null && (entry.Path == _book.Opf.Path || entry.Path == _book.Opf.NcxPath || entry.Path == _book.Opf.NavPath
                                             || entry.Path == EpubPackage.ContainerPath || entry.Path == EpubPackage.MimetypePath);
        var view = FileViews.Create(entry, editable: !structural);
        if (view is ICSharpCode.AvalonEdit.TextEditor ed && !structural)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_book == null) return;
                _book.SetFileText(entry.Path, ed.Text);
                _recoveryDirty = true;
                UpdateDirtyState();
                UpdateStatus();
                if (EpubPath.Extension(entry.Path) == ".css" && ActiveChapterForPreview() is { } c) Preview.Reload(c, _lastPreviewBlock);
            };
            ed.Document.TextChanged += (_, _) =>
            {
                timer.Stop();
                timer.Start();
            };
        }
        return view;
    }

    private Chapter? ActiveChapterForPreview() =>
        _tabs.Select(t => t.Editor?.Chapter).LastOrDefault(c => c != null && c.Path == Preview.CurrentPath) ?? (_currentChapter >= 0 && _book != null ? _book.Chapters[_currentChapter] : null);

    private void FlushAllEditors()
    {
        foreach (var t in _tabs) t.Editor?.FlushCode();
    }

    // ================================================================== saving

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void OnSaveAsClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    /// <summary>Saves the book (backup of the previous file first). Returns false if cancelled or failed.</summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_book == null) return false;
        FlushAllEditors();
        var book = _book;

        var broken = book.Chapters.Where(c => c.ParseError != null).ToList();
        if (broken.Count > 0 && MessageBox.Show(this, T.F("Save_BrokenXhtml", string.Join("\n", broken.Select(c => $"• {c.Index + 1}. {c.Title}"))),
                "Kaku", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        var target = book.FilePath;
        if (saveAs)
        {
            var dlg = new SaveFileDialog
            {
                Filter = T.Get("Dialog_EpubFilter"),
                Title = T.Get("Dialog_SaveAsTitle"),
                FileName = Path.GetFileName(book.FilePath),
                InitialDirectory = Path.GetDirectoryName(book.FilePath),
                DefaultExt = ".epub",
            };
            if (dlg.ShowDialog(this) != true) return false;
            target = dlg.FileName;
        }
        else if (!book.IsModified)
        {
            StatusText.Text = T.Get("Status_NothingToSave");
            return true;
        }
        else if (File.Exists(target) && Stamp(target) != _bookStamp
                 && MessageBox.Show(this, T.F("Save_ChangedOnDisk", target), "Kaku", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return false;
        }

        var oldPath = book.FilePath;
        Mouse.OverrideCursor = Cursors.Wait;
        string? backup;
        try
        {
            backup = await Task.Run(() =>
            {
                var b = Backups.Create(target, Path.Combine(AppSettings.LocalFolder, "Backups"));
                book.Save(target);
                return b;
            });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, T.F("Save_Failed", target, ex.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _bookStamp = Stamp(target);
        DeleteRecovery(oldPath);
        DeleteRecovery(target);
        if (saveAs)
        {
            _bookmarkStore.Move(oldPath, target);
            _referenceStore.Move(oldPath, target);
            _referenceStore.Save();
            _bookmarkStore.Save();
            _settings.AddRecent(target);
            _settings.Save();
            RebuildRecentMenu();
        }
        UpdateDirtyState();
        foreach (var item in _chapterItems) item.RefreshState();
        UpdateStatus();
        StatusText.Text = backup == null ? T.F("Status_Saved", Path.GetFileName(target)) : T.F("Status_SavedWithBackup", Path.GetFileName(target));
        return true;
    }

    /// <summary>Asks what to do with unsaved changes. False = the user cancelled (keep the book open).</summary>
    private bool ConfirmDiscardChanges()
    {
        if (_book == null) return true;
        FlushAllEditors();
        if (!_book.IsModified) return true;
        switch (MessageBox.Show(this, T.F("Confirm_SaveChanges", _book.Title), "Kaku", MessageBoxButton.YesNoCancel, MessageBoxImage.Question))
        {
            case MessageBoxResult.Yes:
                // modal wait: saving is fast and the window must not close before it finishes
                var task = SaveAsync(saveAs: false);
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);
                return task.Result;
            case MessageBoxResult.No:
                DeleteRecovery(_book.FilePath);
                return true;
            default:
                return false;
        }
    }

    private static (long, DateTime) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, default);
    }

    private void OnOpenBackupsClick(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppSettings.LocalFolder, "Backups");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // ================================================================== crash recovery

    private static string RecoveryRoot => Path.Combine(AppSettings.LocalFolder, "Recovery");

    private void WriteRecovery()
    {
        if (_book == null || !_recoveryDirty) return;
        _recoveryDirty = false;
        try
        {
            FlushAllEditors();
            if (_book.IsModified) Recovery.Write(RecoveryRoot, _book);
            else Recovery.Delete(RecoveryRoot, _book.FilePath);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static void DeleteRecovery(string bookPath)
    {
        try
        {
            Recovery.Delete(RecoveryRoot, bookPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The book was opened from its recovery copy: show what changed.</summary>
    private void AfterRecovered(Book book)
    {
        var first = book.Chapters.FirstOrDefault(c => c.IsModified);
        if (first != null) OpenChapter(first.Index, pin: true, location: null, focus: true);
        _recoveryDirty = true;
        UpdateDirtyState();
        foreach (var item in _chapterItems) item.RefreshState();
        UpdateStatus();
        StatusText.Text = T.Get("Status_Recovered");
    }

    /// <summary>At startup: Kaku did not close cleanly last time → offer to reopen the book with its edits.</summary>
    public async Task CheckRecoveryOnStartupAsync()
    {
        foreach (var data in Recovery.All(RecoveryRoot).ToList())
        {
            if (Recovery.Find(RecoveryRoot, data.BookPath) == null)
            {
                DeleteRecovery(data.BookPath); // the book changed or vanished: the edits no longer apply
                continue;
            }
            if (MessageBox.Show(this, T.F("Recovery_Startup", data.Title, data.SavedAt.ToString("g", T.Es)), "Kaku",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await OpenBookAsync(data.BookPath, recover: true);
                return;
            }
            DeleteRecovery(data.BookPath);
        }
    }
}
