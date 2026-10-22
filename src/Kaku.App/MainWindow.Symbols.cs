using System.Media;
using System.Windows;
using System.Windows.Threading;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Navigation;

namespace Kaku.App;

/// <summary>The «Símbolos» tab and the last reading position of each book.</summary>
public partial class MainWindow
{
    private const int MaxRecentSymbols = 16;
    private readonly PositionStore _positionStore = PositionStore.Load();
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private ReadingPosition? _openedPosition;
    private string? _lastSavedPositionKey;

    private void InitSymbolsAndPosition()
    {
        SymbolsView.SetRecent(_settings.RecentSymbols);
        SymbolsView.InsertRequested += InsertSymbol;
        SymbolsView.Copied += s => StatusText.Text = T.F("Symbols_Copied", Views.SymbolsPanel.Describe(s));
        _positionTimer.Tick += (_, _) => SavePosition();
        _positionTimer.Start();
    }

    // ================================================================== symbols

    private void InsertSymbol(string symbol)
    {
        // the chapter (or CSS) tab in front, even though the click put the focus in the panel
        bool done = ActiveTab?.Editor is { } ed ? ed.InsertText(symbol)
            : ActiveTab?.View is ICSharpCode.AvalonEdit.TextEditor { IsReadOnly: false } code && InsertInto(code, symbol);
        if (!done)
        {
            SystemSounds.Beep.Play();
            StatusText.Text = T.Get("Symbols_NoEditor");
            return;
        }
        _settings.RecentSymbols.Remove(symbol);
        _settings.RecentSymbols.Insert(0, symbol);
        if (_settings.RecentSymbols.Count > MaxRecentSymbols) _settings.RecentSymbols.RemoveRange(MaxRecentSymbols, _settings.RecentSymbols.Count - MaxRecentSymbols);
        SymbolsView.SetRecent(_settings.RecentSymbols);
    }

    private static bool InsertInto(ICSharpCode.AvalonEdit.TextEditor code, string text)
    {
        if (code.TextArea.Selection.IsEmpty) code.Document.Insert(code.CaretOffset, text);
        else code.TextArea.Selection.ReplaceSelectionWithText(text);
        code.Focus();
        return true;
    }

    private void OnSymbolsPanelClick(object sender, RoutedEventArgs e) => ShowSymbolsPanel();

    private void ShowSymbolsPanel()
    {
        if (_book != null) SymbolsView.SetBook(_book);
        SetBottomPanel(true);
        BottomTabs.SelectedItem = SymbolsTab;
    }

    // ================================================================== last position

    /// <summary>Remembers where the cursor is (every few seconds, and when the book closes).</summary>
    private void SavePosition()
    {
        if (_book == null || ActiveTab?.Editor is not { } ed || ed.Chapter.Blocks.Count == 0) return;
        var loc = ed.CurrentLocation;
        var key = $"{_book.FilePath}|{ed.Chapter.Path}|{loc.Block}|{loc.Offset}";
        if (key == _lastSavedPositionKey) return;
        _lastSavedPositionKey = key;
        _positionStore.Set(_book.FilePath, ReadingPosition.Capture(_book, loc));
        _positionStore.Save();
    }

    /// <summary>Where to open a book: its last position, or null for the default (first real chapter).</summary>
    private TextLocation? LastPositionFor(Book book)
    {
        _openedPosition = _positionStore.Get(book.FilePath);
        if (_openedPosition == null) return null;
        var (loc, how) = _openedPosition.Resolve(book);
        var c = book.Chapters[loc.Chapter];
        StatusText.Text = T.F(how == ReadingPosition.Match.Percent ? "Position_Approx" : "Position_Restored", loc.Chapter + 1, c.Title, loc.Block + 1);
        return loc;
    }

    private void OnLastPositionClick(object sender, RoutedEventArgs e) => GoToOpenedPosition();

    /// <summary>Ir → Volver a la última posición: where the book was left in the previous session.</summary>
    private void GoToOpenedPosition()
    {
        if (_book == null) return;
        if (_openedPosition == null)
        {
            StatusText.Text = T.Get("Position_None");
            return;
        }
        var (loc, _) = _openedPosition.Resolve(_book);
        JumpTo(loc);
    }

    /// <summary>"45,8 %" for the recent books menu (the book is not opened, so only the saved percentage is known).</summary>
    private string? PositionLabel(string path) =>
        _positionStore.Get(path) is { } p ? T.F("Position_Label", T.N1(p.Percent)) : null;
}
