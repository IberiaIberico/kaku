using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Notes;
using Kaku.Core.Text;

namespace Kaku.App.Views;

/// <summary>All notes of the book: filter, jump, edit, delete; placement and marker style; convert foreign notes.</summary>
public partial class NotesPanel : UserControl
{
    private Book? _book;
    private NoteIndexResult? _index;
    private bool _loading;

    public sealed record Row(BookNote Note, string NumberText, string ChapterText, string Context, string Text);

    public NotesPanel()
    {
        InitializeComponent();
    }

    public event Action<BookNote>? JumpToMarker;
    public event Action<BookNote>? JumpToNote;
    public event Action<BookNote>? EditRequested;
    public event Action<BookNote>? DeleteRequested;
    public event Action<NotePlacement, NoteMarkerStyle>? SettingsChanged;
    public event Action? RenumberRequested;
    public event Action? ConvertRequested;

    public NoteIndexResult? Index => _index;

    public void Load(Book? book)
    {
        _book = book;
        if (book == null)
        {
            _index = null;
            List.ItemsSource = null;
            CountText.Text = "";
            return;
        }
        var keep = (List.SelectedItem as Row)?.Note.Uid;
        _index = NoteIndex.Build(book);
        _loading = true;
        PlacementBox.SelectedIndex = _index.Placement == NotePlacement.EndOfChapter ? 1 : 0;
        StyleBox.SelectedIndex = (int)_index.Style;
        _loading = false;
        Fill();
        if (keep != null && ((IEnumerable<Row>)List.ItemsSource).FirstOrDefault(r => r.Note.Uid == keep) is { } row) List.SelectedItem = row;
        ConvertButton.Content = T.F("Notes_Convert", _index.Foreign.Count);
        ConvertButton.Visibility = _index.Foreign.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.ToolTip = string.Join("\n", _index.Foreign.Take(8).Select(f => $"«{f.MarkerText}» {f.Context} → {Shorten(f.Text, 60)}"));
    }

    private void Fill()
    {
        if (_index == null || _book == null) return;
        var q = TextNormalizer.Fold(FilterBox.Text.Trim());
        var rows = _index.Notes.Select(n =>
        {
            var c = n.Markers.Count > 0 ? _book.ChapterByPath(n.Markers[0].ChapterPath) : null;
            return new Row(n,
                NoteIndex.MarkerText(_index.Style, n.Number) + (n.Markers.Count > 1 ? $" ×{n.Markers.Count}" : ""),
                c == null ? T.Get("Notes_NoMarker") : $"{c.Index + 1}. {c.Title}",
                n.Markers.FirstOrDefault()?.Context ?? "",
                n.Text);
        }).Where(r => q.Length == 0 || TextNormalizer.Fold(r.Text + " " + r.Context + " " + r.ChapterText).Contains(q)).ToList();
        List.ItemsSource = rows;
        CountText.Text = _index.Notes.Count == 0
            ? T.Get("Notes_None")
            : T.F("Notes_Count", _index.Notes.Count, _index.Placement == NotePlacement.EndOfChapter ? T.Get("Notes_PlacementChapter") : T.Get("Notes_PlacementPage"))
              + (_index.OrphanMarkers.Count > 0 ? " · " + T.F("Notes_Orphans", _index.OrphanMarkers.Count) : "");
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private BookNote? Selected => (List.SelectedItem as Row)?.Note;

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Fill();

    private void OnSettingsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _index == null || !IsLoaded) return;
        var placement = PlacementBox.SelectedIndex == 1 ? NotePlacement.EndOfChapter : NotePlacement.NotesPage;
        var style = (NoteMarkerStyle)Math.Max(0, StyleBox.SelectedIndex);
        if (placement == _index.Placement && style == _index.Style) return;
        SettingsChanged?.Invoke(placement, style);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) EditRequested?.Invoke(n);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) DeleteRequested?.Invoke(n);
    }

    private void OnGoToNote(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) JumpToNote?.Invoke(n);
    }

    private void OnRenumber(object sender, RoutedEventArgs e) => RenumberRequested?.Invoke();

    private void OnConvert(object sender, RoutedEventArgs e) => ConvertRequested?.Invoke();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } n) JumpToMarker?.Invoke(n);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Selected is not { } n) return;
        if (e.Key == Key.Enter) { JumpToMarker?.Invoke(n); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteRequested?.Invoke(n); e.Handled = true; }
    }
}
