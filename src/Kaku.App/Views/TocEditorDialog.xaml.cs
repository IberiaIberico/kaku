using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Structure;

namespace Kaku.App.Views;

/// <summary>One editable row of the TOC editor.</summary>
public sealed partial class TocRow : ObservableObject
{
    public TocRow(TocDraft d, string targetText)
    {
        _title = d.Title;
        _level = d.Level;
        Path = d.Path;
        Fragment = d.Fragment;
        BlockIndex = d.BlockIndex;
        TargetText = targetText;
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent))]
    private int _level;

    public string Path { get; }
    public string? Fragment { get; }
    public int BlockIndex { get; }
    public string TargetText { get; }
    public Thickness Indent => new(Level * 22, 1, 0, 1);

    public TocDraft ToDraft() => new(Title, Path, Fragment, Level, BlockIndex);
}

/// <summary>Edit the table of contents: titles, order, levels; generate from headings.</summary>
public partial class TocEditorDialog : Window
{
    private readonly Book _book;
    private readonly BookStructure _structure;
    private readonly TextLocation? _current;
    private readonly ObservableCollection<TocRow> _rows = new();

    public TocEditorDialog(Window owner, Book book, BookStructure structure, TextLocation? current)
    {
        InitializeComponent();
        Owner = owner;
        _book = book;
        _structure = structure;
        _current = current;
        AddCurrentButton.IsEnabled = current != null;
        Load(structure.TocDrafts());
        Rows.ItemsSource = _rows;
    }

    public List<TocDraft> Result => _rows.Select(r => r.ToDraft()).ToList();

    private void Load(IEnumerable<TocDraft> drafts)
    {
        _rows.Clear();
        foreach (var d in drafts) _rows.Add(new TocRow(d, Target(d)));
    }

    private string Target(TocDraft d)
    {
        var c = _book.ChapterByPath(d.Path);
        var file = Kaku.Core.Epub.EpubPath.FileName(d.Path);
        var where = c == null ? file : $"{c.Index + 1}. {file}";
        return d.Fragment != null ? $"{where}#{d.Fragment}" : d.BlockIndex > 0 ? $"{where} · {T.F("Toc_Paragraph", d.BlockIndex + 1)}" : where;
    }

    private TocRow? Selected => Rows.SelectedItem as TocRow;

    private void Move(int delta)
    {
        if (Selected is not { } r) return;
        int i = _rows.IndexOf(r), j = i + delta;
        if (j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
        Rows.SelectedItem = r;
        Rows.ScrollIntoView(r);
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnDown(object sender, RoutedEventArgs e) => Move(+1);

    private void OnIndent(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        int i = _rows.IndexOf(r);
        int max = i == 0 ? 0 : _rows[i - 1].Level + 1;
        r.Level = Math.Min(max, r.Level + 1);
    }

    private void OnOutdent(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) r.Level = Math.Max(0, r.Level - 1);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        int i = _rows.IndexOf(r);
        _rows.Remove(r);
        if (_rows.Count > 0) Rows.SelectedIndex = Math.Min(i, _rows.Count - 1);
    }

    private void OnAddCurrent(object sender, RoutedEventArgs e)
    {
        if (_current is not { } loc) return;
        var c = _book.Chapters[loc.Chapter];
        var block = c.Blocks.Count > loc.Block ? c.Blocks[loc.Block] : null;
        var title = block is { Kind: Kaku.Core.Text.BlockKind.Heading } ? Chapter_Shorten(block.Text) : c.Title;
        var id = (string?)block?.Element?.Attribute("id");
        var draft = loc.Block == 0 ? new TocDraft(title, c.Path, null, 0) : new TocDraft(title, c.Path, id, 1, id == null ? loc.Block : -1);
        // insert in reading order
        int pos = _rows.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            var rc = _book.ChapterByPath(_rows[i].Path);
            if (rc != null && rc.Index > loc.Chapter) { pos = i; break; }
        }
        var row = new TocRow(draft, Target(draft));
        _rows.Insert(pos, row);
        Rows.SelectedItem = row;
        Rows.ScrollIntoView(row);
    }

    private static string Chapter_Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_rows.Count > 0 && MessageBox.Show(this, T.Get("Toc_ConfirmGenerate"), "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Load(_structure.GenerateToc());
    }

    private void OnSort(object sender, RoutedEventArgs e)
    {
        var order = _rows.Select((r, i) => (r, i))
            .OrderBy(x => _book.ChapterByPath(x.r.Path)?.Index ?? int.MaxValue)
            .ThenBy(x => x.r.Fragment != null ? _book.ChapterByPath(x.r.Path)?.IdToBlock.GetValueOrDefault(x.r.Fragment, 0) ?? 0 : Math.Max(0, x.r.BlockIndex))
            .ThenBy(x => x.i).Select(x => x.r).ToList();
        _rows.Clear();
        foreach (var r in order) _rows.Add(r);
    }

    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Right: OnIndent(sender, e); e.Handled = true; break;
            case Key.Left: OnOutdent(sender, e); e.Handled = true; break;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_rows.Any(r => r.Title.Trim().Length == 0))
        {
            MessageBox.Show(this, T.Get("Toc_EmptyTitle"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
