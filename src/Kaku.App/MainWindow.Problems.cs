using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Checks;
using Kaku.Core.Editing;
using Microsoft.Win32;

namespace Kaku.App;

/// <summary>Problems panel: text checks, spelling and EPUB validation; fixes go through the chapter undo.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _checksTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private CancellationTokenSource? _checksCts;
    private List<Problem> _epubcheckProblems = new();

    private void InitProblems()
    {
        Problems.Spelling = _settings.SpellCheck;
        SpellCheckMenu.IsChecked = _settings.SpellCheck;
        Problems.RefreshRequested += () => _ = RunChecksAsync();
        Problems.ProblemActivated += ActivateProblem;
        Problems.FixRequested += ApplyFixes;
        Problems.SuggestionChosen += (p, s) => ApplyFixes([p with { Fix = new TextChange(p.Start, p.Start + p.Length, s) }]);
        Problems.EpubcheckRequested += () => _ = RunEpubcheckAsync();
        Problems.SpellingToggled += on => SetSpellCheck(on);
        SpellService.WordsChanged += () =>
        {
            foreach (var t in _tabs) t.Editor?.RedrawSpelling();
        };
        _checksTimer.Tick += (_, _) =>
        {
            _checksTimer.Stop();
            _ = RunChecksAsync();
        };
        // the dictionary loads in the background; underline once it is ready
        _ = SpellService.GetAsync().ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            foreach (var t in _tabs) t.Editor?.RedrawSpelling();
        }));
    }

    /// <summary>The spell checker learns the book's names (so they are not underlined), then the editors redraw.</summary>
    private void LearnBookNames(Book book)
    {
        var texts = book.Chapters.SelectMany(c => c.Blocks.Select(b => b.WordText)).ToList();
        _ = Task.Run(async () =>
        {
            if (await SpellService.GetAsync() is not { } spell) return;
            spell.LearnNames(texts);
            await Dispatcher.BeginInvoke(() =>
            {
                foreach (var t in _tabs) t.Editor?.RedrawSpelling();
            });
        });
    }

    private bool ProblemsVisible => BottomPanel.Visibility == Visibility.Visible && BottomTabs.SelectedItem == ProblemsTab;

    private void OnProblemsPanelClick(object sender, RoutedEventArgs e) => ShowProblemsPanel();

    private void ShowProblemsPanel()
    {
        if (_book == null) return;
        SetBottomPanel(true);
        BottomTabs.SelectedItem = ProblemsTab;
        _ = RunChecksAsync();
    }

    /// <summary>Called after edits: re-check later if the panel is showing.</summary>
    private void ScheduleChecks()
    {
        if (!ProblemsVisible) return;
        _checksTimer.Stop();
        _checksTimer.Start();
    }

    private async Task RunChecksAsync()
    {
        if (_book == null)
        {
            Problems.SetProblems(null, []);
            return;
        }
        _checksCts?.Cancel();
        var cts = _checksCts = new CancellationTokenSource();
        FlushAllEditors();
        var book = _book;
        bool wholeBook = Problems.WholeBook;
        int current = ActiveTab?.Editor?.Chapter.Index ?? _currentChapter;
        var all = TextChecks.Snapshot(book);
        var dominant = TextChecks.DominantQuote(all);
        var snapshot = wholeBook || current < 0 ? all : all.Where(b => b.Chapter == current).ToList();
        var spell = Problems.Spelling ? await SpellService.GetAsync() : null;
        if (spell != null && !wholeBook) spell.LearnNames(all.Select(b => b.WordText)); // names from the whole book
        Problems.SetBusy(T.Get("Problems_Running"));
        List<Problem> problems;
        try
        {
            problems = await Task.Run(() =>
            {
                var list = TextChecks.Run(snapshot, wholeBook ? spell : null, dominant, cts.Token);
                if (!wholeBook && spell != null)
                    foreach (var b in snapshot.Where(b => !b.IsEmpty))
                        list.AddRange(spell.Check(b.WordText).Select(m => new Problem(CheckKind.Spelling, Severity.Warning, $"Ortografía: «{m.Word}»",
                            b.Chapter, b.Block, m.Start, m.Length, b.Revision, Context: Context(b.Text, m.Start, m.Length), Word: m.Word)));
                return list;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || _book != book) return;
        if (wholeBook) problems.AddRange(EpubValidator.Run(book));
        problems.AddRange(_epubcheckProblems);
        Problems.SetProblems(book, problems);
    }

    private static string Context(string text, int start, int length)
    {
        int a = Math.Max(0, start - 35), z = Math.Min(text.Length, start + length + 45);
        return (a > 0 ? "…" : "") + text[a..z] + (z < text.Length ? "…" : "");
    }

    private void ActivateProblem(Problem p)
    {
        if (_book == null) return;
        if (p.Chapter >= 0 && p.Chapter < _book.Chapters.Count)
        {
            RecordJump();
            var ed = OpenChapter(p.Chapter, pin: false, location: new TextLocation(p.Chapter, p.Block, p.Start), focus: true);
            if (p.Length > 0) ed.SelectText(p.Block, p.Start, p.Block, p.Start + p.Length);
            return;
        }
        if (p.Path != null && _book.Package.Exists(p.Path)) JumpToSource(p.Path, p.Line);
    }

    /// <summary>Applies automatic fixes, chapter by chapter (one undo step per chapter). Stale problems are skipped.</summary>
    private void ApplyFixes(List<Problem> problems)
    {
        if (_book == null) return;
        FlushAllEditors();
        int applied = 0, skipped = 0;
        var changed = new List<Chapter>();
        foreach (var byChapter in problems.Where(p => p.Fix != null && p.Chapter >= 0).GroupBy(p => p.Chapter))
        {
            var c = _book.Chapters[byChapter.Key];
            var fresh = byChapter.Where(p => p.Revision == c.Revision).ToList();
            skipped += byChapter.Count() - fresh.Count;
            if (fresh.Count == 0) continue;
            // one change per position, no overlaps inside a block
            var perBlock = new Dictionary<int, List<TextChange>>();
            foreach (var g in fresh.GroupBy(p => p.Block))
            {
                var list = new List<TextChange>();
                int lastEnd = int.MaxValue;
                foreach (var f in g.Select(p => p.Fix!.Value).Distinct().OrderByDescending(f => f.Start))
                {
                    if (f.End > lastEnd) { skipped++; continue; }
                    list.Add(f);
                    lastEnd = f.Start;
                }
                perBlock[g.Key] = list;
            }
            var before = c.SourceText;
            var editor = new ChapterEditor(_book, c);
            var rejected = editor.ApplyTextChangesBatch(perBlock);
            foreach (var (bi, list) in perBlock)
            {
                if (!rejected.Contains(bi)) applied += list.Count;
            }
            foreach (var bi in rejected.OrderByDescending(b => b))
            {
                if (bi < c.Blocks.Count && editor.ApplyTextChanges(bi, perBlock[bi])) applied += perBlock[bi].Count;
                else skipped += perBlock[bi].Count;
            }
            if (c.SourceText != before)
            {
                HistoryFor(c).Record(new EditSnapshot(before, new TextLocation(c.Index, 0, 0), 0, false), null);
                changed.Add(c);
            }
        }
        _book.RefreshPositions();
        AfterBulkChange(changed, []);
        StatusText.Text = skipped == 0 ? T.F("Status_Fixed", applied) : T.F("Status_FixedSkipped", applied, skipped);
        _ = RunChecksAsync();
    }

    // ================================================================== spelling while typing

    private void OnSpellCheckMenuClick(object sender, RoutedEventArgs e) => SetSpellCheck(SpellCheckMenu.IsChecked);

    private void SetSpellCheck(bool on)
    {
        _settings.SpellCheck = on;
        _settings.Save();
        SpellCheckMenu.IsChecked = on;
        Problems.Spelling = on;
        foreach (var t in _tabs)
            if (t.Editor is { } ed) ed.SpellingEnabled = on;
        if (ProblemsVisible) _ = RunChecksAsync();
    }

    private void OnUserDictionaryClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        if (!File.Exists(SpellService.UserFile)) SpellService.Checker?.WriteUserFile(SpellService.UserFile);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SpellService.UserFile) { UseShellExecute = true });
        MessageBox.Show(this, T.Get("Spell_UserFileInfo"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================================================================== epubcheck

    private void OnEpubcheckClick(object sender, RoutedEventArgs e) => _ = RunEpubcheckAsync();

    private async Task RunEpubcheckAsync()
    {
        if (_book == null) return;
        var java = EpubValidator.FindJava();
        if (java == null)
        {
            MessageBox.Show(this, T.Get("Epubcheck_NoJava"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var jar = _settings.EpubcheckJar;
        if (jar == null || !File.Exists(jar))
        {
            var dlg = new OpenFileDialog { Title = T.Get("Epubcheck_PickJar"), Filter = "epubcheck.jar|epubcheck.jar|*.jar|*.jar" };
            if (dlg.ShowDialog(this) != true) return;
            jar = dlg.FileName;
            _settings.EpubcheckJar = jar;
            _settings.Save();
        }
        FlushAllEditors();
        SetBottomPanel(true);
        BottomTabs.SelectedItem = ProblemsTab;
        Problems.SetBusy(T.Get("Epubcheck_Running"));
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var (problems, error) = await EpubValidator.RunEpubcheck(_book, java, jar);
            if (error != null)
            {
                MessageBox.Show(this, T.F("Epubcheck_Failed", error), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _epubcheckProblems = problems;
            StatusText.Text = problems.Count == 0 ? T.Get("Epubcheck_Clean") : T.F("Epubcheck_Count", problems.Count);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        await RunChecksAsync();
    }

    private bool HandleProblemsShortcut(Key key, ModifierKeys mods)
    {
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M)
        {
            ShowProblemsPanel();
            return true;
        }
        return false;
    }
}
