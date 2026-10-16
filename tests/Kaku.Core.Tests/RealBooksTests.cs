using System.IO.Compression;
using Kaku.Core.Epub;
using Kaku.Core.Navigation;
using Kaku.Core.Preview;
using Xunit.Abstractions;

namespace Kaku.Core.Tests;

/// <summary>Runs against every EPUB in the translate workspace (kobo_downloads/ and out/), if present.</summary>
public class RealBooksTests(ITestOutputHelper output)
{
    internal static IEnumerable<string> BookPaths() => Books();

    private static IEnumerable<string> Books()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "kobo_downloads"))) dir = dir.Parent;
        if (dir == null) yield break;
        foreach (var sub in new[] { "kobo_downloads", "out" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir.FullName, sub), "*.epub"))
                yield return f;
    }

    [Fact]
    public void Open_save_reopen_is_lossless()
    {
        foreach (var path in Books())
        {
            var original = EpubPackage.Load(path);
            var tmp = Path.Combine(Path.GetTempPath(), $"kaku-test-{Guid.NewGuid():N}.epub");
            try
            {
                original.Save(tmp);
                var reloaded = EpubPackage.Load(tmp);
                Assert.Equal(original.Entries.Select(e => e.Path).Order(), reloaded.Entries.Select(e => e.Path).Order());
                foreach (var e in original.Entries)
                    Assert.True(e.Data.AsSpan().SequenceEqual(reloaded.Find(e.Path)!.Data), $"{Path.GetFileName(path)}: {e.Path} differs");

                // mimetype must be the first entry, stored, without extra field (byte 38 rule)
                var head = File.ReadAllBytes(tmp).AsSpan(0, 58).ToArray();
                Assert.Equal("PK\u0003\u0004", System.Text.Encoding.ASCII.GetString(head, 0, 4));
                Assert.Equal(0, BitConverter.ToUInt16(head, 8));   // compression method: stored
                Assert.Equal(8, BitConverter.ToUInt16(head, 26));  // name length
                Assert.Equal(0, BitConverter.ToUInt16(head, 28));  // extra field length
                Assert.Equal("mimetypeapplication/epub+zip", System.Text.Encoding.ASCII.GetString(head, 30, 28));
                using var zip = ZipFile.OpenRead(tmp);
                Assert.Equal("mimetype", zip.Entries[0].FullName);
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void Typing_in_the_largest_chapter_is_fast()
    {
        var all = Books().Select(Book.Load).SelectMany(b => b.Chapters.Select(c => (Book: b, Chapter: c))).ToList();
        if (all.Count == 0) return;
        var (book, c) = all.MaxBy(x => x.Chapter.SourceText.Length);
        var ed = new Kaku.Core.Editing.ChapterEditor(book, c);
        int bi = c.Blocks.FindIndex(b => b.WordCount > 5);
        ed.ApplyTextChanges(bi, [new Kaku.Core.Editing.TextChange(0, 0, "x")]); // warm-up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) ed.ApplyTextChanges(bi, [new Kaku.Core.Editing.TextChange(0, 0, "x")]);
        double ms = sw.Elapsed.TotalMilliseconds / 20;
        output.WriteLine($"{c.Path}: {c.SourceText.Length / 1024} KB, {c.Blocks.Count} blocks → {ms:F1} ms per keystroke");
        Assert.True(ms < 40, $"{ms:F1} ms per keystroke");
    }

    [Fact]
    public void Random_edits_on_real_books_stay_consistent()
    {
        var rnd = new Random(7);
        foreach (var path in Books())
        {
            var book = Book.Load(path);
            foreach (var c in book.Chapters.Where(c => c.WordCount > 200).Take(4))
            {
                var ed = new Kaku.Core.Editing.ChapterEditor(book, c);
                for (int i = 0; i < 25; i++)
                {
                    int bi = rnd.Next(c.Blocks.Count);
                    var b = c.Blocks[bi];
                    var textChars = Enumerable.Range(0, b.Text.Length).Where(k => b.Spans.All(s => !s.IsPlaceholder || k < s.Start || k >= s.End)).ToList();
                    if (textChars.Count == 0) continue;
                    int at = textChars[rnd.Next(textChars.Count)];
                    var expected = b.Text.Insert(at, "Ñx");
                    Assert.True(ed.ApplyTextChanges(bi, [new Kaku.Core.Editing.TextChange(at, at, "Ñx")]), $"{c.Path} block {bi}");
                    Assert.Equal(expected, c.Blocks[bi].Text);

                    // split and merge back: same text again
                    var tag = c.Blocks[bi].Tag;
                    if (tag == "p" && ed.SplitBlock(bi, at + 1))
                    {
                        Assert.True(ed.MergeWithPrevious(bi + 1), $"{c.Path} merge {bi}");
                        Assert.Equal(expected, c.Blocks[bi].Text);
                    }
                }
                Assert.Null(c.ParseError);
                Assert.True(c.IsModified);
            }
            // untouched chapters keep their exact source
            Assert.All(book.Chapters.Where(c => !c.IsModified), c => Assert.Equal(c.SavedText, c.SourceText));
        }
    }

    [Fact]
    public void Books_open_with_text_and_consistent_positions()
    {
        foreach (var path in Books())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var book = Book.Load(path);
            sw.Stop();
            output.WriteLine($"{Path.GetFileName(path)}: {book.Chapters.Count} chapters, {book.Chapters.Sum(c => c.Blocks.Count)} blocks, " +
                             $"{book.TotalWords} words, {book.Toc.Count} toc entries, {sw.ElapsedMilliseconds} ms");

            Assert.NotEmpty(book.Chapters);
            Assert.True(book.TotalWords > 1000, $"{path}: too few words");
            Assert.All(book.Chapters, c => Assert.Null(c.ParseError));
            Assert.All(book.Chapters, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));

            // chapter tree: parents come earlier in reading order; continuations are named after their parent
            Assert.All(book.Chapters.Where(c => c.Parent != null), c => Assert.True(c.Parent!.Index < c.Index));
            Assert.All(book.Chapters.Where(c => c.IsContinuation), c => Assert.StartsWith(c.Parent!.Title + " (", c.Title));

            // word index ↔ location round trip
            var rnd = new Random(42);
            for (int i = 0; i < 300; i++)
            {
                long w = rnd.NextInt64(book.TotalWords);
                Assert.Equal(w, book.WordIndexAt(book.LocationOfWord(w)));
            }
            Assert.Equal(0, book.WordIndexAt(book.LocationOfPercent(0)));
            var half = GoToParser.Parse(book, "50%")!;
            Assert.InRange(book.PercentAt(half.Location), 49.9, 50.1);
            Assert.Equal(GoToKind.Word, GoToParser.Parse(book, "palabra 1.000")!.Kind);
            Assert.Equal(999, book.WordIndexAt(GoToParser.Parse(book, "w1000")!.Location));
            Assert.Null(GoToParser.Parse(book, (book.Chapters.Count + 1).ToString()));
            Assert.Equal(new TextLocation(1, 0, 0), GoToParser.Parse(book, "2")!.Location);

            // preview markup stays parseable and tags every block
            foreach (var c in book.Chapters.Take(15))
            {
                var html = System.Text.Encoding.UTF8.GetString(PreviewBuilder.Build(c, c.SourceText));
                var doc = XmlLoader.Parse(html);
                var tagged = doc.Descendants().Count(e => e.Attribute(PreviewBuilder.BlockAttribute) != null);
                Assert.True(tagged >= c.Blocks.Select(b => b.Element).Distinct().Count(), $"{c.Path}: {tagged} tagged");
            }
        }
    }
}
