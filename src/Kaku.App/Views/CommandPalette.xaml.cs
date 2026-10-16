using System.Windows;
using System.Windows.Input;
using Kaku.Core.Text;

namespace Kaku.App.Views;

public sealed record PaletteItem(string Kind, string Title, string Detail, Action Execute, string SearchText = "")
{
    public string MatchText => SearchText.Length > 0 ? SearchText : Title;
}

/// <summary>Ctrl+P: type part of a chapter, heading, file or command name and press Enter.</summary>
public partial class CommandPalette : Window
{
    private const int MaxResults = 80;
    private readonly List<PaletteItem> _items;
    private readonly List<PaletteItem> _initial;
    private bool _closing;

    public CommandPalette(Window owner, List<PaletteItem> items, List<PaletteItem> initial)
    {
        InitializeComponent();
        Owner = owner;
        _items = items;
        _initial = initial;
        Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
        Top = owner.Top + 70;
        Loaded += (_, _) =>
        {
            Update();
            Query.Focus();
        };
        Deactivated += (_, _) => SafeClose();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Update()
    {
        var q = Query.Text;
        List<PaletteItem> list;
        if (q.Trim().Length == 0)
        {
            list = _initial.Take(MaxResults).ToList();
        }
        else
        {
            list = _items
                .Select(i => (Item: i, Score: FuzzyFilter.Score(q, i.MatchText)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
        }
        Results.ItemsSource = list;
        if (list.Count > 0) Results.SelectedIndex = 0;
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Update();

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        int count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
            case Key.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageDown when count > 0:
                Results.SelectedIndex = Math.Min(count - 1, Results.SelectedIndex + 10);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageUp when count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 10);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Execute();
                e.Handled = true;
                break;
        }
    }

    private void OnResultsClick(object sender, MouseButtonEventArgs e) => Execute();

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e) => Execute();

    private void Execute()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        SafeClose();
        Owner?.Activate();
        item.Execute();
    }
}
