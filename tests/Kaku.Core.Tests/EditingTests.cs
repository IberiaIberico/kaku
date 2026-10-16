using System.IO.Compression;
using System.Text;
using Kaku.Core.Editing;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Tests;

public class EditingTests
{
    private const string Head = "<?xml version='1.0' encoding='UTF-8'?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"es\">\n<head>\n<title>t</title>\n</head>\n<body class='p-text'>\n<div class=\"main\">\n";
    private const string Tail = "\n</div>\n</body>\n</html>\n";

    /// <summary>A one-chapter book built in memory.</summary>
    internal static (Book Book, Chapter Chapter, ChapterEditor Editor) Make(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaku-edit-{Guid.NewGuid():N}.epub");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("OEBPS/content.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>T</dc:title></metadata><manifest><item id=\"c1\" href=\"Text/c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>");
            Add("OEBPS/Text/c1.xhtml", Head + body + Tail);
        }
        var book = Book.Load(path);
        File.Delete(path);
        return (book, book.Chapters[0], new ChapterEditor(book, book.Chapters[0]));
    }

    private static string Body(Chapter c)
    {
        var s = c.SourceText;
        Assert.StartsWith(Head, s);
        Assert.EndsWith(Tail, s);
        return s[Head.Length..^Tail.Length];
    }

    [Fact]
    public void Typing_keeps_inline_markup_and_the_rest_of_the_file()
    {
        var (_, c, ed) = Make("<p class=\"x\">Hola <span class=\"koboSpan\" id=\"k1\">mundo</span> cruel.</p>\n<p>Otro</p>");
        Assert.Equal("Hola mundo cruel.", c.Blocks[0].Text);
        Assert.True(ed.ApplyTextChanges(0, [new TextChange(5, 10, "planeta")]));
        Assert.Equal("<p class=\"x\">Hola <span class=\"koboSpan\" id=\"k1\">planeta</span> cruel.</p>\n<p>Otro</p>", Body(c));
        Assert.True(c.IsModified);

        Assert.True(ed.ApplyTextChanges(1, [new TextChange(4, 4, " párrafo & <más>")]));
        Assert.Equal("Otro párrafo & <más>", c.Blocks[1].Text);
        Assert.EndsWith("<p>Otro párrafo &amp; &lt;más&gt;</p>", Body(c));
    }

    [Fact]
    public void Deleting_a_collapsed_space_removes_the_whole_run()
    {
        var (_, c, ed) = Make("<p>uno\n   dos</p>");
        Assert.Equal("uno dos", c.Blocks[0].Text);
        Assert.True(ed.ApplyTextChanges(0, [new TextChange(3, 4, "")]));
        Assert.Equal("unodos", c.Blocks[0].Text);
        Assert.Equal("<p>unodos</p>", Body(c));
    }

    [Fact]
    public void Images_cannot_be_edited_as_text_but_text_around_them_can()
    {
        var (_, c, ed) = Make("<p>a<img src=\"../i.png\" alt=\"\"/>b</p>");
        var b = c.Blocks[0];
        int img = b.Spans.Single(s => s.Kind == SpanKind.Image).Start;
        Assert.False(ed.ApplyTextChanges(0, [new TextChange(img, img + 1, "")]));
        Assert.True(ed.ApplyTextChanges(0, [new TextChange(0, 1, "X"), new TextChange(b.Text.Length - 1, b.Text.Length, "Y")]));
        Assert.Equal("<p>X<img src=\"../i.png\" alt=\"\"/>Y</p>", Body(c));
    }

    [Fact]
    public void Typing_into_an_empty_paragraph()
    {
        var (_, c, ed) = Make("<p></p>\n<p><br/></p>");
        Assert.Equal(2, c.Blocks.Count);
        Assert.True(ed.ApplyTextChanges(0, [new TextChange(0, 0, "Nuevo")]));
        Assert.True(ed.ApplyTextChanges(1, [new TextChange(1, 1, "tras br")]));
        Assert.Equal("<p>Nuevo</p>\n<p><br/>tras br</p>", Body(c));
    }

    [Fact]
    public void Split_and_merge_round_trip()
    {
        var (_, c, ed) = Make("<p class=\"x\" id=\"p1\">Hola <b>gran</b> mundo</p>\n<p>Fin</p>");
        Assert.True(ed.SplitBlock(0, 7)); // "Hola gr|an mundo"
        Assert.Equal(3, c.Blocks.Count);
        Assert.Equal("Hola gr", c.Blocks[0].Text);
        Assert.Equal("an mundo", c.Blocks[1].Text);
        Assert.Equal("<p class=\"x\" id=\"p1\">Hola <b>gr</b></p>\n<p class=\"x\"><b>an</b> mundo</p>\n<p>Fin</p>", Body(c));

        Assert.True(ed.MergeWithPrevious(1));
        Assert.Equal(2, c.Blocks.Count);
        Assert.Equal("Hola gran mundo", c.Blocks[0].Text);
        Assert.Equal("<p class=\"x\" id=\"p1\">Hola <b>gr</b><b>an</b> mundo</p>\n<p>Fin</p>", Body(c));
    }

    [Fact]
    public void Enter_at_end_of_heading_creates_a_paragraph()
    {
        var (_, c, ed) = Make("<h2 class=\"t\">Título</h2>");
        Assert.True(ed.SplitBlock(0, 6));
        Assert.Equal("<h2 class=\"t\">Título</h2>\n<p></p>", Body(c));
        Assert.Equal(BlockKind.Paragraph, c.Blocks[1].Kind);
        Assert.True(ed.ApplyTextChanges(1, [new TextChange(0, 0, "Texto")]));
        Assert.Equal("Texto", c.Blocks[1].Text);
    }

    [Fact]
    public void Merge_refuses_blocks_in_different_containers()
    {
        var (_, _, ed) = Make("<div><p>a</p></div><div><p>b</p></div>");
        Assert.False(ed.CanMergeWithPrevious(1));
    }

    [Fact]
    public void Delete_range_across_paragraphs()
    {
        var (_, c, ed) = Make("<p>uno dos</p>\n<p>tres</p>\n<p>cuatro cinco</p>");
        Assert.True(ed.DeleteRange(0, 3, 2, 6)); // "uno| dos / tres / cuatro| cinco"
        Assert.Single(c.Blocks);
        Assert.Equal("uno cinco", c.Blocks[0].Text);
    }

    [Fact]
    public void Toggle_bold_wraps_and_unwraps()
    {
        var (_, c, ed) = Make("<p>Hola mundo</p>");
        Assert.True(ed.ToggleFormat(0, 5, 10, SpanKind.Bold));
        Assert.Equal("<p>Hola <b>mundo</b></p>", Body(c));
        Assert.True(ed.ToggleFormat(0, 6, 8, SpanKind.Bold)); // inside bold: removes it
        Assert.Equal("<p>Hola mundo</p>", Body(c));
        Assert.True(ed.ToggleFormat(0, 0, 4, SpanKind.Italic));
        Assert.Equal("<p><i>Hola</i> mundo</p>", Body(c));
    }

    [Fact]
    public void Broken_source_keeps_the_last_good_text_model()
    {
        var (_, c, ed) = Make("<p>bien</p>");
        var good = c.SourceText;
        Assert.NotNull(ed.SetSource(good.Replace("</p>", "")));
        Assert.Equal("bien", c.Blocks[0].Text);
        Assert.False(ed.ApplyTextChanges(0, [new TextChange(0, 0, "x")]));
        Assert.Null(ed.SetSource(good));
        Assert.False(c.IsModified);
    }

    [Fact]
    public void History_groups_typing_and_undoes_in_order()
    {
        var h = new EditHistory();
        var t0 = DateTime.UtcNow;
        h.Record(new EditSnapshot("a", default, 0, false), "text:0", t0);
        h.Record(new EditSnapshot("ab", default, 0, false), "text:0", t0.AddMilliseconds(300)); // same step
        h.Record(new EditSnapshot("abc", default, 0, false), null, t0.AddMilliseconds(600));    // new step
        Assert.Equal("abc", h.Undo(new EditSnapshot("abcd", default, 0, false))!.Source);
        Assert.Equal("a", h.Undo(new EditSnapshot("abc", default, 0, false))!.Source);
        Assert.False(h.CanUndo);
        Assert.Equal("abc", h.Redo(new EditSnapshot("a", default, 0, false))!.Source);
    }

    [Fact]
    public void Scanner_ranges_match_every_element()
    {
        var (_, c, _) = Make("<p>a <ruby>漢<rt>かん</rt></ruby><!-- x <p> --><![CDATA[<b>]]><br/></p>");
        foreach (var e in c.Document!.Root!.DescendantsAndSelf())
        {
            var r = c.RangeOf(e)!.Value;
            Assert.StartsWith("<" + (e.Name.LocalName), c.SourceText[r.OuterStart..].Replace("<epub:", "<"));
            if (!r.SelfClosing) Assert.EndsWith(">", c.SourceText[r.OuterStart..r.OuterEnd]);
        }
    }

    [Fact]
    public void Save_writes_edits_and_keeps_other_entries_identical()
    {
        var (book, c, ed) = Make("<p>Hola</p>");
        var before = book.Package.Entries.ToDictionary(e => e.Path, e => e.Data.ToArray());
        Assert.True(ed.ApplyTextChanges(0, [new TextChange(4, 4, " mundo")]));
        Assert.True(book.IsModified);
        var path = Path.Combine(Path.GetTempPath(), $"kaku-save-{Guid.NewGuid():N}.epub");
        try
        {
            book.Save(path);
            Assert.False(book.IsModified);
            var again = Book.Load(path);
            Assert.Equal("Hola mundo", again.Chapters[0].Blocks[0].Text);
            foreach (var e in again.Package.Entries.Where(e => e.Path != c.Path))
                Assert.Equal(before[e.Path], e.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Recovery_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kaku-rec-{Guid.NewGuid():N}");
        var (book, _, ed) = Make("<p>Hola</p>");
        var path = Path.Combine(Path.GetTempPath(), $"kaku-recbook-{Guid.NewGuid():N}.epub");
        try
        {
            book.Save(path);
            ed.ApplyTextChanges(0, [new TextChange(0, 4, "Adiós")]);
            Recovery.Write(root, book);

            var data = Recovery.Find(root, path)!;
            var fresh = Recovery.Open(root, data);
            Assert.Equal(path, fresh.FilePath);
            Assert.Equal("Adiós", fresh.Chapters[0].Blocks[0].Text);
            Assert.True(fresh.IsModified);
            Recovery.Delete(root, path);
            Assert.Null(Recovery.Find(root, path));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
