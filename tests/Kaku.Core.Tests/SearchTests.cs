using Kaku.Core.Navigation;
using Kaku.Core.Search;
using Kaku.Core.Text;
using Xunit.Abstractions;

namespace Kaku.Core.Tests;

public class SearchTests(ITestOutputHelper output)
{
    private static (Book Book, Chapter Chapter) Make(string body)
    {
        var (b, c, _) = EditingTests.Make(body);
        return (b, c);
    }

    [Fact]
    public void Text_search_options()
    {
        var (book, _) = Make("<p>El Árbol y el arbolito.</p><p>ARBOL <i>grande</i> en el camión.</p>");
        int Count(SearchQuery q) => BookSearch.Find(book, q).Hits.Count;
        Assert.Equal(2, Count(new SearchQuery("arbol")));                                // case-insensitive, accent-sensitive
        Assert.Equal(3, Count(new SearchQuery("arbol", IgnoreAccents: true)));            // Árbol, arbol(ito), ARBOL
        Assert.Equal(2, Count(new SearchQuery("arbol", IgnoreAccents: true, WholeWord: true)));
        Assert.Equal(1, Count(new SearchQuery("ARBOL", MatchCase: true)));
        Assert.Equal(1, Count(new SearchQuery("ARBOL grande", MatchCase: true)));         // across <i>
        Assert.Equal(1, Count(new SearchQuery(@"cami[oó]n\.", UseRegex: true)));
        Assert.NotNull(BookSearch.Find(book, new SearchQuery("(", UseRegex: true)).Error);
    }

    [Fact]
    public void Text_search_ignores_tags_and_image_names_but_code_search_sees_them()
    {
        var (book, _) = Make("<p class=\"png\">foto <img src=\"../a.png\" alt=\"\"/> aquí</p>");
        Assert.Empty(BookSearch.Find(book, new SearchQuery("png")).Hits);
        var code = BookSearch.Find(book, new SearchQuery("png", Scope: SearchScope.Code)).Hits;
        Assert.Equal(2, code.Count(h => h.Chapter == 0));
        Assert.All(code, h => Assert.True(h.Line > 0));
    }

    [Fact]
    public void Replace_in_text_keeps_markup_and_expands_groups()
    {
        var (book, c) = Make("<p>Dijo «hola» y <b>«adiós»</b>.</p>");
        var q = new SearchQuery("«([^»]*)»", UseRegex: true);
        var hits = BookSearch.Find(book, q).Hits;
        Assert.Equal(2, hits.Count);
        var r = BookSearch.Replace(book, q, hits, "“$1”");
        Assert.Equal(2, r.Replaced);
        Assert.Contains("<p>Dijo “hola” y <b>“adiós”</b>.</p>", c.SourceText);
        Assert.Single(r.ChapterUndo);
    }

    [Fact]
    public void Accent_insensitive_replace_keeps_the_original_letters_in_groups()
    {
        var (book, c) = Make("<p>Kōtō dijo</p>");
        var q = new SearchQuery("(koto) dijo", IgnoreAccents: true, UseRegex: true);
        var r = BookSearch.Replace(book, q, BookSearch.Find(book, q).Hits, "$1 respondió");
        Assert.Equal(1, r.Replaced);
        Assert.Equal("Kōtō respondió", c.Blocks[0].Text);
    }

    [Fact]
    public void Code_replace_that_breaks_xhtml_is_skipped()
    {
        var (book, c) = Make("<p>uno</p>");
        var before = c.SourceText;
        var q = new SearchQuery("</p>", Scope: SearchScope.Code);
        var r = BookSearch.Replace(book, q, BookSearch.Find(book, q).Hits, "");
        Assert.Equal(0, r.Replaced);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(before, c.SourceText);
    }

    [Fact]
    public void Stale_hits_are_not_replaced()
    {
        var (book, c) = Make("<p>uno uno</p>");
        var q = new SearchQuery("uno");
        var hits = BookSearch.Find(book, q).Hits;
        new Kaku.Core.Editing.ChapterEditor(book, c).ApplyTextChanges(0, [new Kaku.Core.Editing.TextChange(0, 0, "x")]);
        var r = BookSearch.Replace(book, q, hits, "dos");
        Assert.Equal(0, r.Replaced);
        Assert.Equal(2, r.Skipped);
    }

    [Fact]
    public void Navigation_history_back_and_forward()
    {
        var h = new NavigationHistory();
        var a = new TextLocation(0, 10, 0);
        var b = new TextLocation(3, 50, 0);
        var c = new TextLocation(5, 1, 0);
        h.Record(a);      // jump a → b
        h.Record(b);      // jump b → c
        Assert.Equal(b, h.Back(c));
        Assert.Equal(a, h.Back(b));
        Assert.Null(h.Back(a));
        Assert.Equal(b, h.Forward(a));
        Assert.Equal(c, h.Forward(b));
    }

    [Fact]
    public void Replace_all_on_a_real_book_is_fast_and_complete()
    {
        var path = RealBooksTests.BookPaths().FirstOrDefault(p => p.Contains("Grasshopper"));
        if (path == null) return;
        var book = Book.Load(path);
        var q = new SearchQuery("Suzuki", WholeWord: true);
        var hits = BookSearch.Find(book, q).Hits;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = BookSearch.Replace(book, q, hits, "Suzuki-kun");
        output.WriteLine($"{hits.Count} hits → {r.Replaced} replaced, {r.Skipped} skipped in {sw.ElapsedMilliseconds} ms");
        Assert.Equal(hits.Count, r.Replaced + r.Skipped);
        Assert.True(r.Replaced >= hits.Count * 0.99);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Empty(BookSearch.Find(book, new SearchQuery(@"Suzuki(?!-kun)", UseRegex: true, MatchCase: true)).Hits.Where(h => r.Skipped == 0));
    }

    /// <summary>Passages copied from the e-reader: lowercase, no accents, other quotes, a word missing, a typo, across paragraphs.</summary>
    [Fact]
    public void Passage_finder_locates_garbled_snippets_in_real_books()
    {
        var rnd = new Random(11);
        int tried = 0, found = 0;
        foreach (var path in RealBooksTests.BookPaths().Where(p => p.Contains("out")))
        {
            var book = Book.Load(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var finder = new PassageFinder(book);
            output.WriteLine($"{Path.GetFileName(path)}: index {sw.ElapsedMilliseconds} ms");
            var words = book.Chapters.SelectMany(c => c.Blocks.Where(b => !b.IsNote).SelectMany(b =>
                WordCounter.Words(b.WordText).Select(w => (c, b, w.Start, w.Length)))).ToList();
            for (int i = 0; i < 25; i++)
            {
                int at = rnd.Next(words.Count - 30);
                int len = 7 + rnd.Next(10);
                var span = words.Skip(at).Take(len).Select(w => w.b.WordText.Substring(w.Start, w.Length)).ToList();
                // garble it
                if (rnd.Next(2) == 0 && span.Count > 6) span.RemoveAt(3);
                var typoAt = span.FindIndex(w => w.Length >= 6);
                if (typoAt >= 0 && rnd.Next(2) == 0) span[typoAt] = span[typoAt][..^2] + span[typoAt][^1] + span[typoAt][^2];
                var text = "«" + BookSearch.FoldAccents(string.Join(rnd.Next(2) == 0 ? " " : "\n", span)).ToLowerInvariant() + "»";

                tried++;
                sw.Restart();
                var best = finder.Find(text).FirstOrDefault();
                var ms = sw.ElapsedMilliseconds;
                Assert.True(ms < 500, $"{ms} ms");
                long expected = book.WordIndexAt(new TextLocation(words[at].c.Index, words[at].b.Index, words[at].Start));
                if (best != null && Math.Abs(best.WordIndex - expected) <= 2) found++;
                else output.WriteLine($"  miss: {text}");
            }
        }
        output.WriteLine($"found {found}/{tried}");
        Assert.True(found >= tried * 0.95, $"found {found}/{tried}");
    }
}
