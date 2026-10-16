using Kaku.Core.Editing;
using Kaku.Core.Reference;
using Xunit.Abstractions;

namespace Kaku.Core.Tests;

public class ReferenceTests(ITestOutputHelper output)
{
    /// <summary>(translated, original) pairs found among the test books, by structure.</summary>
    private static IEnumerable<(Book Es, string JaPath)> Pairs()
    {
        var paths = RealBooksTests.BookPaths().ToList();
        var jaFolder = paths.Select(Path.GetDirectoryName).FirstOrDefault(d => d!.EndsWith("kobo_downloads"));
        if (jaFolder == null) yield break;
        foreach (var es in paths.Where(p => p.Contains(Path.DirectorySeparatorChar + "out" + Path.DirectorySeparatorChar)))
        {
            var book = Book.Load(es);
            // search both folders: the Spanish copies must not outrank the Japanese original
            var outFolder = Path.GetDirectoryName(es)!;
            var best = ReferenceBook.FindCandidates(book, [jaFolder, outFolder]).FirstOrDefault();
            if (best != null && best.Score > 2.0) yield return (book, best.Path);
        }
    }

    [Fact]
    public void Finds_the_original_and_aligns_unedited_books_one_to_one()
    {
        int pairs = 0;
        foreach (var (es, jaPath) in Pairs())
        {
            pairs++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reference = ReferenceBook.Load(jaPath);
            // the right edition: every translated test book has a known original
            var expectedOriginal = Path.GetFileName(es.FilePath) switch
            {
                var n when n.StartsWith("Grasshopper") => "グラスホッパー",
                var n when n.StartsWith("Materiales") => "可燃物",
                var n when n.StartsWith("onajiyume") || n.StartsWith("Soñé") => "また同じ夢",
                var n when n.StartsWith("Unsocial") || n.StartsWith("unsocial") => "アンソーシャル",
                _ => "",
            };
            Assert.Contains(expectedOriginal, Path.GetFileName(jaPath));
            int same = 0, total = 0, unmatchedChapters = 0;
            double coverage = 0;
            foreach (var c in es.Chapters)
            {
                var o = reference.ChapterFor(es, c);
                if (o == null) { unmatchedChapters++; continue; }
                var a = reference.AlignmentFor(es, c)!;
                coverage += a.Coverage;
                if (o.Blocks.Count != c.Blocks.Count) continue; // edited in Kaku: no one-to-one expectation
                for (int i = 0; i < c.Blocks.Count; i++)
                {
                    if (c.Blocks[i].IsNote || c.Blocks[i].WordCount == 0) continue;
                    total++;
                    if (a.ToOriginal[i] == i) same++;
                }
            }
            output.WriteLine($"{Path.GetFileName(es.FilePath)} ↔ {Path.GetFileName(jaPath)}: one-to-one {same}/{total} " +
                             $"({100.0 * same / Math.Max(1, total):F1} %), chapters without original {unmatchedChapters}, {sw.ElapsedMilliseconds} ms");
            Assert.True(same >= total * 0.97, $"{same}/{total}");
        }
        Assert.True(pairs >= 3, $"only {pairs} pairs found");
    }

    [Fact]
    public void Survives_deleted_and_split_paragraphs()
    {
        var (es, jaPath) = Pairs().First(p => p.Es.Title.Contains("Material", StringComparison.OrdinalIgnoreCase) || p.Es.FilePath.Contains("Materiales"));
        var reference = ReferenceBook.Load(jaPath);
        var c = es.Chapters.MaxBy(x => x.Blocks.Count)!;
        var editor = new ChapterEditor(es, c);
        // remember what original paragraph some sample paragraphs map to
        int[] probes = [40, 120, 200, 300];
        var expected = probes.ToDictionary(i => c.Blocks[i].Text, i => i);
        // delete paragraph 100 (merge its text away) and split paragraph 150 in two
        Assert.True(editor.DeleteRange(99, c.Blocks[99].Text.Length, 100, c.Blocks[100].Text.Length));
        Assert.True(editor.SplitBlock(150, c.Blocks[150].Text.Length / 2));
        var a = reference.AlignmentFor(es, c)!;
        int ok = 0;
        foreach (var (text, original) in expected)
        {
            int now = c.Blocks.FindIndex(b => b.Text == text);
            output.WriteLine($"block now {now} → original {a.ToOriginal[now]} (expected {original})");
            if (a.ToOriginal[now] == original) ok++;
        }
        Assert.Equal(expected.Count, ok);
    }

    [Fact]
    public void Pins_override_the_alignment()
    {
        var (es, jaPath) = Pairs().First();
        var reference = ReferenceBook.Load(jaPath);
        var c = es.Chapters.Where(x => reference.ChapterFor(es, x) != null).MaxBy(x => x.Blocks.Count)!;
        reference.Pins[c.Path] = [new Aligner.Pin(10, 20)];
        reference.Invalidate(c);
        var a = reference.AlignmentFor(es, c)!;
        Assert.Equal(20, a.ToOriginal[10]);
        Assert.True(a.ToOriginal[11] >= 20);
        Assert.True(a.ToOriginal[9] <= 20);
    }
}
