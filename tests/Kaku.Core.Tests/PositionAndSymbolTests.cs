using Kaku.Core.Editing;
using Kaku.Core.Navigation;
using Kaku.Core.Structure;
using Kaku.Core.Text;

namespace Kaku.Core.Tests;

public class PositionAndSymbolTests
{
    private static Book RealBook() => Book.Load(RealBooksTests.BookPaths().First(p => p.Contains("Materiales")));

    [Fact]
    public void Same_book_resolves_exactly()
    {
        var book = RealBook();
        var c = book.Chapters.MaxBy(x => x.Blocks.Count)!;
        var loc = new TextLocation(c.Index, 150, 12);
        var p = ReadingPosition.Capture(book, loc);
        var (found, how) = p.Resolve(book);
        Assert.Equal(loc, found);
        Assert.Equal(ReadingPosition.Match.Exact, how);
    }

    [Fact]
    public void Survives_paragraphs_inserted_before_and_a_split_chapter()
    {
        var book = RealBook();
        var c = book.Chapters.MaxBy(x => x.Blocks.Count)!;
        var loc = new TextLocation(c.Index, 300, 20);
        var text = c.Blocks[300].Text;
        var p = ReadingPosition.Capture(book, loc);

        // two new paragraphs earlier in the chapter: the paragraph moves to 302
        var ed = new ChapterEditor(book, c);
        Assert.True(ed.SplitBlock(10, 5));
        Assert.True(ed.SplitBlock(20, 5));
        var (moved, how) = p.Resolve(book);
        Assert.Equal(ReadingPosition.Match.Moved, how);
        Assert.Equal(text, book.Chapters[moved.Chapter].Blocks[moved.Block].Text);
        Assert.Equal(20, moved.Offset);

        // the chapter is split before it: now it is in another file
        new BookStructure(book).Split(c, 200);
        var (again, how2) = p.Resolve(book);
        Assert.Equal(ReadingPosition.Match.Moved, how2);
        Assert.StartsWith(text[20..40], book.Chapters[again.Chapter].Blocks[again.Block].Text[again.Offset..]);
    }

    [Fact]
    public void Edited_paragraph_keeps_the_cursor_near_the_same_words()
    {
        var book = RealBook();
        var c = book.Chapters.MaxBy(x => x.Blocks.Count)!;
        int bi = c.Blocks.FindIndex(b => b.Text.Length > 200);
        var p = ReadingPosition.Capture(book, new TextLocation(c.Index, bi, 100));
        var after = c.Blocks[bi].Text[100..140];
        new ChapterEditor(book, c).ApplyTextChanges(bi, [new TextChange(95, 99, "XXXXXXXX")]); // edit just before the cursor
        var (loc, _) = p.Resolve(book);
        Assert.Equal(bi, loc.Block);
        Assert.StartsWith(after, c.Blocks[bi].Text[loc.Offset..]);
    }

    [Fact]
    public void Unknown_text_falls_back_to_percent()
    {
        var book = RealBook();
        var p = new ReadingPosition { ChapterPath = "nope.xhtml", Snippet = "zzzz qqqq xxxx no existe en ningún libro de verdad", Percent = 50 };
        var (loc, how) = p.Resolve(book);
        Assert.Equal(ReadingPosition.Match.Percent, how);
        Assert.InRange(book.PercentAt(loc), 49.5, 50.5);
    }

    [Fact]
    public void Symbols_are_counted_without_ordinary_letters()
    {
        var (book, _, _) = EditingTests.Make("<p>—Hola —dijo Kōtō— «¿qué?» … 「えっ」 n.º 3 años</p><p>Señor &amp; señora…</p>");
        var s = SymbolStats.Count(book).ToDictionary(x => x.Symbol, x => x.Count);
        Assert.Equal(3, s["—"]);
        Assert.Equal(2, s["ō"]);
        Assert.Equal(2, s["…"]);
        Assert.Equal(1, s["「"]);
        Assert.Equal(1, s["&"]);
        Assert.Equal(1, s["º"]);
        Assert.False(s.ContainsKey("ñ"));
        Assert.False(s.ContainsKey("え"));
        Assert.False(s.ContainsKey("."));
    }
}
