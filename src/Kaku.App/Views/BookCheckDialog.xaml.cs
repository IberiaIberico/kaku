using System.Windows;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Structure;

namespace Kaku.App.Views;

/// <summary>Broken links, missing/unlisted/unused files, with one-click fixes. Double-click jumps to the source.</summary>
public partial class BookCheckDialog : Window
{
    private readonly Book _book;

    public sealed record Row(BookProblem Problem)
    {
        public string KindText => T.Get("Check_" + Problem.Kind);
        public string Detail => Problem.Detail;
        public string Path => Problem.Path;
        public string WhereText => Problem.Source == null ? "" : $"{EpubPath.FileName(Problem.Source)}:{Problem.Line}";
    }

    public BookCheckDialog(Window owner, Book book)
    {
        InitializeComponent();
        Owner = owner;
        _book = book;
        Refresh();
    }

    /// <summary>A fix the window must apply (it owns the structure history and refreshes the views).</summary>
    public event Action<ProblemKind, List<string>>? FixRequested;

    /// <summary>Jump to (file, line).</summary>
    public event Action<string, int>? JumpRequested;

    public void Refresh()
    {
        var problems = BookCheck.Run(_book);
        List.ItemsSource = problems.OrderBy(p => p.Kind).ThenBy(p => p.Source).ThenBy(p => p.Line).Select(p => new Row(p)).ToList();
        int Count(ProblemKind k) => problems.Count(p => p.Kind == k);
        Summary.Text = problems.Count == 0 ? T.Get("Check_AllGood") : T.F("Check_Summary", problems.Count);
        Setup(DeleteUnusedButton, "Check_DeleteUnused", Count(ProblemKind.Unused));
        Setup(AddUnlistedButton, "Check_AddUnlisted", Count(ProblemKind.NotInManifest));
        Setup(RemoveMissingButton, "Check_RemoveMissing", Count(ProblemKind.MissingFile));
    }

    private static void Setup(System.Windows.Controls.Button b, string key, int n)
    {
        b.Content = T.F(key, n);
        b.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<string> PathsOf(ProblemKind k) =>
        ((IEnumerable<Row>)List.ItemsSource).Where(r => r.Problem.Kind == k).Select(r => r.Problem.Path).Distinct().ToList();

    private void OnDeleteUnused(object sender, RoutedEventArgs e)
    {
        var paths = PathsOf(ProblemKind.Unused);
        if (MessageBox.Show(this, T.F("Check_ConfirmDelete", paths.Count, string.Join("\n", paths.Take(15))), "Kaku", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        FixRequested?.Invoke(ProblemKind.Unused, paths);
        Refresh();
    }

    private void OnAddUnlisted(object sender, RoutedEventArgs e)
    {
        FixRequested?.Invoke(ProblemKind.NotInManifest, PathsOf(ProblemKind.NotInManifest));
        Refresh();
    }

    private void OnRemoveMissing(object sender, RoutedEventArgs e)
    {
        FixRequested?.Invoke(ProblemKind.MissingFile, PathsOf(ProblemKind.MissingFile));
        Refresh();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Jump();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Jump();
    }

    private void Jump()
    {
        if (List.SelectedItem is not Row r) return;
        var p = r.Problem;
        JumpRequested?.Invoke(p.Source ?? p.Path, p.Line);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
