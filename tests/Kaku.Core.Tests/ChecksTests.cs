using Kaku.Core.Checks;
using Xunit.Abstractions;

namespace Kaku.Core.Tests;

public class ChecksTests(ITestOutputHelper output)
{
    private static readonly Lazy<SpellChecker> Spell = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Kaku.App"))) dir = dir.Parent;
        var d = Path.Combine(dir!.FullName, "src", "Kaku.App", "Resources", "Dictionaries");
        using var dic = File.OpenRead(Path.Combine(d, "es_ES.dic"));
        using var aff = File.OpenRead(Path.Combine(d, "es_ES.aff"));
        return new SpellChecker(dic, aff);
    });

    private static List<Problem> Check(string body, bool spell = false)
    {
        var (book, _, _) = EditingTests.Make(body);
        return TextChecks.Run(TextChecks.Snapshot(book), spell ? Spell.Value : null);
    }

    private static string Apply(string text, Kaku.Core.Editing.TextChange c) => text[..c.Start] + c.Insert + text[c.End..];

    [Fact]
    public void Finds_typical_translation_leftovers()
    {
        var p = Check("<p>Dijo que que no , pero... 「hola」 &lt;|im_end|&gt; y 東京.</p><p>- ¿Vienes?</p><p>Hola,amigo.Bien</p>");
        Assert.Contains(p, x => x.Kind == CheckKind.RepeatedWord);
        Assert.Contains(p, x => x.Kind == CheckKind.SpaceBeforePunct);
        Assert.Contains(p, x => x.Kind == CheckKind.ThreeDots);
        Assert.Equal(2, p.Count(x => x.Kind == CheckKind.JapaneseQuotes));
        Assert.Contains(p, x => x.Kind == CheckKind.ModelTokens);
        Assert.Contains(p, x => x.Kind == CheckKind.Japanese && x.Message.Contains("東京"));
        Assert.Contains(p, x => x.Kind == CheckKind.HyphenDialogue);
        Assert.Equal(2, p.Count(x => x.Kind == CheckKind.MissingSpaceAfterPunct));
    }

    [Fact]
    public void Fixes_produce_the_expected_text()
    {
        var text = "Dijo que que no , pero...";
        var p = Check($"<p>{text}</p>");
        Assert.Equal("Dijo que no , pero...", Apply(text, p.First(x => x.Kind == CheckKind.RepeatedWord).Fix!.Value));
        Assert.Equal("Dijo que que no, pero...", Apply(text, p.First(x => x.Kind == CheckKind.SpaceBeforePunct).Fix!.Value));
        Assert.Equal("Dijo que que no , pero…", Apply(text, p.First(x => x.Kind == CheckKind.ThreeDots).Fix!.Value));
        var h = Check("<p>- Hola.</p>").Single(x => x.Kind == CheckKind.HyphenDialogue);
        Assert.Equal("—Hola.", Apply("- Hola.", h.Fix!.Value));
    }

    [Fact]
    public void Quote_style_follows_the_book()
    {
        var p = Check("<p>«Uno» y «dos» y «tres».</p><p>“Cuatro” y \"cinco\".</p>");
        Assert.Equal(2, p.Count(x => x.Kind == CheckKind.MixedQuotes));
        var straight = p.Where(x => x.Kind == CheckKind.StraightQuotes).ToList();
        Assert.Equal(["«", "»"], straight.Select(x => x.Fix!.Value.Insert));
        Assert.DoesNotContain(p, x => x.Kind == CheckKind.MixedQuotes && x.Context.Contains("Uno"));
    }

    [Fact]
    public void Unpaired_marks_and_empty_paragraphs()
    {
        var p = Check("<p>Vienes?</p><p>¡Hola!</p><p></p><p><br/></p><p></p><p>Fin.</p>");
        Assert.Single(p, x => x.Kind == CheckKind.UnpairedMarks);
        Assert.Single(p, x => x.Kind == CheckKind.EmptyParagraphs);
    }

    [Fact]
    public void Spelling_skips_names_acronyms_and_japanese()
    {
        var p = Check("<p>Suzuki caminaba despacio por Tokio con la ONU. Un erorr aquí. Kōtō no.</p><p>Hola mundo cruel, dijo Suzuki-kun.</p>", spell: true);
        var words = p.Where(x => x.Kind == CheckKind.Spelling).Select(x => x.Word).ToList();
        Assert.Equal(["erorr"], words);
        Assert.Contains("error", Spell.Value.Suggest("erorr"));
    }

    [Fact]
    public void User_words_are_accepted()
    {
        var s = Spell.Value;
        Assert.False(s.IsCorrect("tsundere"));
        s.AddUserWord("tsundere");
        Assert.True(s.IsCorrect("tsundere"));
        Assert.True(s.IsCorrect("Tsundere"));
        s.RemoveUserWord("tsundere");
        Assert.False(s.IsCorrect("tsundere"));
    }

    [Fact]
    public void Epub_validation_reports_metadata_and_duplicate_ids()
    {
        var (book, _, _) = EditingTests.Make("<p id=\"a\">x</p><p id=\"a\">y</p><img src=\"../i.png\"/>");
        var p = EpubValidator.Run(book);
        Assert.Contains(p, x => x.Message.Contains("Id repetido «a»"));
        Assert.Contains(p, x => x.Message.Contains("dc:identifier"));
        Assert.Contains(p, x => x.Message.Contains("dcterms:modified"));
        Assert.Contains(p, x => x.Message.Contains("alt"));
    }

    [Fact]
    public void Real_books_summary()
    {
        foreach (var path in RealBooksTests.BookPaths().Where(p => p.Contains("out")))
        {
            var book = Book.Load(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snap = TextChecks.Snapshot(book);
            var p = TextChecks.Run(snap, Spell.Value);
            var ms = sw.ElapsedMilliseconds;
            var epub = EpubValidator.Run(book);
            var byKind = string.Join(", ", p.GroupBy(x => x.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            var topWords = string.Join(", ", p.Where(x => x.Kind == CheckKind.Spelling).GroupBy(x => x.Word).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}×{g.Count()}"));
            output.WriteLine($"{Path.GetFileName(path)}: {ms} ms · {byKind} · epub {epub.Count}");
            output.WriteLine($"   spelling top: {topWords}");
            foreach (var e in epub.Where(e => e.Severity != Severity.Info).Take(4)) output.WriteLine("   epub: " + e.Message);
            Assert.True(ms < 5000, $"{ms} ms");
        }
    }
}
