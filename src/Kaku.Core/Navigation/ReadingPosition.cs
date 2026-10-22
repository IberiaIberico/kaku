using Kaku.Core.Search;
using Kaku.Core.Text;

namespace Kaku.Core.Navigation;

/// <summary>
/// Where the user was in a book. Besides chapter/paragraph/offset it keeps a piece of the text around the
/// cursor, so the place can be found again if the book changed (paragraphs added, file renamed, edited elsewhere).
/// </summary>
public sealed class ReadingPosition
{
    public string ChapterPath { get; set; } = "";
    public int Block { get; set; }
    public int Offset { get; set; }
    /// <summary>Text of the paragraph around the cursor, and where the cursor is inside it.</summary>
    public string Snippet { get; set; } = "";
    public int SnippetOffset { get; set; }
    public double Percent { get; set; }
    public DateTime Saved { get; set; }

    private const int Before = 60;
    private const int After = 140;

    public static ReadingPosition Capture(Book book, TextLocation loc)
    {
        var c = book.Chapters[Math.Clamp(loc.Chapter, 0, book.Chapters.Count - 1)];
        var p = new ReadingPosition { ChapterPath = c.Path, Block = loc.Block, Offset = loc.Offset, Percent = book.PercentAt(loc), Saved = DateTime.Now };
        if (loc.Block >= 0 && loc.Block < c.Blocks.Count)
        {
            var text = c.Blocks[loc.Block].Text;
            int off = Math.Clamp(loc.Offset, 0, text.Length);
            int start = Math.Max(0, off - Before);
            p.Snippet = text[start..Math.Min(text.Length, off + After)];
            p.SnippetOffset = off - start;
        }
        return p;
    }

    public enum Match { Exact, Moved, Passage, Percent }

    /// <summary>
    /// Finds the position in the book as it is now: the same paragraph if its text is still there, else the
    /// snippet nearby in the chapter, else anywhere in the book (fuzzy), else the same percentage.
    /// </summary>
    public (TextLocation Location, Match How) Resolve(Book book)
    {
        if (book.Chapters.Count == 0) return (default, Match.Percent);
        var c = book.ChapterByPath(ChapterPath);
        if (c != null && Snippet.Length > 0)
        {
            if (Block >= 0 && Block < c.Blocks.Count && Locate(c.Blocks[Block].Text) is int o)
                return (new TextLocation(c.Index, Block, o), Match.Exact);
            // nearest paragraph of the chapter that still contains the text
            for (int d = 1; d < c.Blocks.Count; d++)
                foreach (var b in new[] { Block - d, Block + d })
                    if (b >= 0 && b < c.Blocks.Count && Locate(c.Blocks[b].Text) is int ob)
                        return (new TextLocation(c.Index, b, ob), Match.Moved);
        }
        if (Snippet.Trim().Length >= 20)
        {
            // the same text in another file (chapter split, renamed or reordered)
            foreach (var other in book.Chapters)
                for (int b = 0; b < other.Blocks.Count; b++)
                    if (other.Blocks[b].Text.Contains(Snippet, StringComparison.Ordinal) && Locate(other.Blocks[b].Text) is int ob)
                        return (new TextLocation(other.Index, b, ob), Match.Moved);
            var best = new PassageFinder(book).Find(Snippet, 1).FirstOrDefault();
            if (best != null && best.Score >= 0.7)
            {
                var text = book.Chapters[best.Start.Chapter].Blocks[best.Start.Block].Text;
                var at = Locate(text) is int o2 ? o2 : Math.Min(text.Length, best.Start.Offset + SnippetOffset);
                return (best.Start with { Offset = at }, Match.Passage);
            }
        }
        if (c != null && Snippet.Length == 0 && Block < c.Blocks.Count)
            return (new TextLocation(c.Index, Math.Max(0, Block), Offset), Match.Exact); // an empty paragraph
        return (book.LocationOfPercent(Percent), Match.Percent);
    }

    /// <summary>Offset of the cursor if the paragraph contains the snippet (or at least its part from the cursor on).</summary>
    private int? Locate(string text)
    {
        int i = text.IndexOf(Snippet, StringComparison.Ordinal);
        if (i >= 0) return i + SnippetOffset;
        // the paragraph was edited around the cursor: try the text that follows it, then the text before it
        var tail = Snippet[SnippetOffset..];
        if (tail.Length >= 25 && text.IndexOf(tail[..Math.Min(tail.Length, 60)], StringComparison.Ordinal) is var j and >= 0) return j;
        var head = Snippet[..SnippetOffset];
        if (head.Length >= 25 && text.IndexOf(head[^Math.Min(head.Length, 60)..], StringComparison.Ordinal) is var k and >= 0) return k + Math.Min(head.Length, 60);
        return null;
    }
}
