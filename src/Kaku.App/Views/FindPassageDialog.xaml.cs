using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Search;

namespace Kaku.App.Views;

/// <summary>Ctrl+Mayús+P: paste what you read on the e-reader, Kaku finds where it is.</summary>
public partial class FindPassageDialog : Window
{
    private static string _lastInput = "";
    private readonly Book _book;
    private readonly PassageFinder _finder;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public sealed record Row(PassageMatch Match, string Header, string ScoreText, Brush ScoreBrush)
    {
        public string Before => Match.Before;
        public string MatchText => Match.Match;
        public string After => Match.After;
    }

    public FindPassageDialog(Window owner, Book book, PassageFinder finder, string? initial)
    {
        InitializeComponent();
        Owner = owner;
        _book = book;
        _finder = finder;
        Input.Text = string.IsNullOrWhiteSpace(initial) ? _lastInput : initial;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Search();
        };
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
            if (Input.Text.Length > 0) Search();
        };
    }

    public PassageMatch? Selected { get; private set; }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Search()
    {
        var text = Input.Text;
        var matches = text.Trim().Length == 0 ? [] : _finder.Find(text);
        Results.ItemsSource = matches.Select(m =>
        {
            var c = _book.Chapters[m.Start.Chapter];
            var header = T.F("Passage_Header", m.Start.Chapter + 1, c.Title, m.Start.Block + 1,
                T.N0(m.WordIndex + 1), T.N1(_book.PercentAt(m.WordIndex)));
            var brush = m.Score >= 0.8 ? Brushes.ForestGreen : m.Score >= 0.55 ? Brushes.DarkGoldenrod : Brushes.Gray;
            return new Row(m, header, $"{Math.Round(m.Score * 100)} %", brush);
        }).ToList();
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
        GoButton.IsEnabled = Results.Items.Count > 0;
        Status.Text = text.Trim().Length == 0 ? "" : Results.Items.Count == 0 ? T.Get("Passage_None") : T.F("Passage_Count", Results.Items.Count);
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        GoButton.IsEnabled = Results.SelectedItem != null;

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Enter jumps (pasted text can still contain line breaks); Mayús+Intro inserts a new line
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            _debounce.Stop();
            Search();
            Go();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            Results.Focus();
            e.Handled = true;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Go();
            e.Handled = true;
        }
    }

    private void OnGo(object sender, RoutedEventArgs e) => Go();

    private void Go()
    {
        if (Results.SelectedItem is not Row row) return;
        _lastInput = Input.Text;
        Selected = row.Match;
        DialogResult = true;
    }
}
