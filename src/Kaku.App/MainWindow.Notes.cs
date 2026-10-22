using System.Windows;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.App.Views;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Notes;

namespace Kaku.App;

/// <summary>Notes inside the book: insert (Ctrl+Alt+N), edit, delete, reuse, panel, placement and style.</summary>
public partial class MainWindow
{
    private void InitNotes()
    {
        NotesView.JumpToMarker += n =>
        {
            if (n.Markers.FirstOrDefault() is { } m && _book?.ChapterByPath(m.ChapterPath) is { } c)
                JumpTo(new TextLocation(c.Index, m.BlockIndex, 0));
        };
        NotesView.JumpToNote += n =>
        {
            if (n.NotePath != null && _book?.ChapterByPath(n.NotePath) is { } c) JumpTo(new TextLocation(c.Index, Math.Max(0, n.NoteBlock), 0));
        };
        NotesView.EditRequested += EditNote;
        NotesView.DeleteRequested += DeleteNote;
        NotesView.SettingsChanged += (placement, style) => Structure(s => s.SetNoteSettings(placement, style));
        NotesView.RenumberRequested += () => Structure(s => s.RenumberNotes());
        NotesView.ConvertRequested += () =>
        {
            var n = NotesView.Index?.Foreign.Count ?? 0;
            if (MessageBox.Show(this, T.F("Notes_ConfirmConvert", n), "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                Structure(s => s.ConvertForeignNotes());
        };
    }

    /// <summary>Text of the note a marker href points to (hover tooltip in text mode).</summary>
    private string? NoteTextFor(string href)
    {
        var id = EpubPath.SplitHref(href).Fragment;
        return CurrentNotes()?.ById(id ?? "")?.Text;
    }

    private NoteIndexResult? _noteCache;
    private int _noteCacheStamp = -1;

    private NoteIndexResult? CurrentNotes()
    {
        if (_book == null) return null;
        int stamp = _book.Chapters.Sum(c => c.Revision) + _book.Chapters.Count * 100_000;
        if (_noteCache == null || stamp != _noteCacheStamp)
        {
            _noteCache = NoteIndex.Build(_book);
            _noteCacheStamp = stamp;
        }
        return _noteCache;
    }

    private void RefreshNotesPanel()
    {
        _noteCache = null;
        if (BottomPanel.Visibility == Visibility.Visible && BottomTabs.SelectedItem == NotesTab) NotesView.Load(_book);
    }

    private void OnBottomTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != BottomTabs) return;
        if (BottomTabs.SelectedItem == NotesTab) NotesView.Load(_book);
        else if (BottomTabs.SelectedItem == ProblemsTab) _ = RunChecksAsync();
        else if (BottomTabs.SelectedItem == SymbolsTab) SymbolsView.SetBook(_book); // counts are refreshed when shown
    }

    private void OnNotesPanelClick(object sender, RoutedEventArgs e) => ShowNotesPanel();

    private void ShowNotesPanel()
    {
        if (_book == null) return;
        SetBottomPanel(true);
        BottomTabs.SelectedItem = NotesTab;
        NotesView.Load(_book);
        NotesView.List.Focus();
    }

    // ================================================================== insert / edit / delete

    private void OnInsertNoteClick(object sender, RoutedEventArgs e) => InsertNote();

    private void InsertNote()
    {
        if (_book == null || ActiveTab?.Editor is not { } ed)
        {
            MessageBox.Show(this, T.Get("Note_NeedEditor"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ed.NoteInsertionPoint() is not var (loc, word))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        var notes = CurrentNotes()?.Notes ?? new();
        var where = T.F("Note_Where", ed.Chapter.Index + 1, ed.Chapter.Title, loc.Block + 1, word.Length > 0 ? "«" + word + "»" : "");
        var dlg = new NoteDialog(this, where, word, notes, null);
        if (dlg.ShowDialog() != true) return;
        var chapter = ed.Chapter;
        var change = Structure(s => s.InsertNote(chapter, loc.Block, loc.Offset, dlg.ContentXhtml ?? "", dlg.ReuseUid));
        if (change == null) return;
        // stay where we were, just after the new marker
        if (_book.ChapterByPath(chapter.Path) is { } c)
        {
            var editor = OpenChapter(c.Index, pin: true, location: new TextLocation(c.Index, loc.Block, loc.Offset + 1), focus: true);
            editor.FocusEditor();
        }
        RefreshNotesPanel();
    }

    /// <summary>Double-click on a marker in text mode.</summary>
    private void OnNoteMarkerActivated(string href)
    {
        var id = EpubPath.SplitHref(href).Fragment;
        if (CurrentNotes()?.ById(id ?? "") is { } note) EditNote(note);
    }

    private void EditNote(BookNote note)
    {
        if (_book == null) return;
        var m = note.Markers.FirstOrDefault();
        var c = m == null ? null : _book.ChapterByPath(m.ChapterPath);
        var where = c == null ? "" : T.F("Note_Where", c.Index + 1, c.Title, m!.BlockIndex + 1, m.Context.Length > 0 ? "«…" + m.Context.TrimStart('…').Split(' ').Last() + "»" : "");
        var dlg = new NoteDialog(this, where, "", new(), note);
        if (dlg.ShowDialog() != true || dlg.ContentXhtml is not { } content || content == note.ContentXhtml) return;
        Structure(s => s.EditNote(note.Uid, content));
        RefreshNotesPanel();
    }

    private void DeleteNote(BookNote note)
    {
        var msg = T.F("Notes_ConfirmDelete", note.Number, note.Text.Length > 120 ? note.Text[..120] + "…" : note.Text)
                  + (note.Markers.Count > 1 ? "\n\n" + T.F("Notes_DeleteMarkers", note.Markers.Count) : "");
        if (MessageBox.Show(this, msg, "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Structure(s => s.DeleteNote(note.Uid));
        RefreshNotesPanel();
    }

    private bool HandleNotesShortcut(Key key, ModifierKeys mods)
    {
        switch (mods, key)
        {
            case (ModifierKeys.Control | ModifierKeys.Alt, Key.N): InsertNote(); return true;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.N): ShowNotesPanel(); return true;
        }
        return false;
    }
}
