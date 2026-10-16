using System.IO.Compression;
using System.Text;
using Kaku.Core.Notes;
using Kaku.Core.Structure;
using Kaku.Core.Text;

namespace Kaku.Core.Tests;

public class NotesTests
{
    private const string Xhtml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head>\n<title>{0}</title>\n</head>\n<body>\n{1}\n</body>\n</html>\n";

    private static Book Make(params string[] bodies)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaku-notes-{Guid.NewGuid():N}.epub");
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
            var items = string.Concat(bodies.Select((_, i) => $"<item id=\"c{i}\" href=\"c{i}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            var refs = string.Concat(bodies.Select((_, i) => $"<itemref idref=\"c{i}\"/>"));
            Add("OEBPS/content.opf", $"<?xml version=\"1.0\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\">\n  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n    <dc:title>T</dc:title>\n    <dc:language>es</dc:language>\n  </metadata>\n  <manifest>\n    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n    {items}\n  </manifest>\n  <spine>\n    {refs}\n  </spine>\n</package>\n");
            Add("OEBPS/nav.xhtml", string.Format(Xhtml, "nav", "<nav xmlns:epub=\"http://www.idpf.org/2007/ops\" epub:type=\"toc\"><ol>" + string.Concat(bodies.Select((_, i) => $"<li><a href=\"c{i}.xhtml\">C{i}</a></li>")) + "</ol></nav>"));
            for (int i = 0; i < bodies.Length; i++) Add($"OEBPS/c{i}.xhtml", string.Format(Xhtml, "C" + i, bodies[i]));
        }
        var book = Book.Load(path);
        File.Delete(path);
        return book;
    }

    private static void AssertClean(Book book)
    {
        var problems = BookCheck.Run(book).Where(p => p.Kind != ProblemKind.Unused).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static int Offset(Chapter c, int block, string after) => c.Blocks[block].Text.IndexOf(after) + after.Length;

    [Fact]
    public void Insert_notes_on_notes_page_numbered_in_reading_order()
    {
        var book = Make("<p>El senpai llegó.</p>", "<p>Otro kōhai más.</p>");
        var s = new BookStructure(book);
        // second chapter first: numbering must still follow reading order
        s.InsertNote(book.Chapters[1], 0, Offset(book.Chapters[1], 0, "kōhai"), MarkdownLite.ToXhtml("Compañero *menor*."));
        s.InsertNote(book.Chapters[0], 0, Offset(book.Chapters[0], 0, "senpai"), MarkdownLite.ToXhtml("Compañero **mayor**."));
        var idx = s.Notes();
        Assert.Equal(2, idx.Notes.Count);
        Assert.Equal("Compañero mayor.", idx.Notes[0].Text);
        Assert.Equal(1, idx.Notes[0].Number);
        Assert.Equal(2, idx.Notes[1].Number);
        Assert.Equal(NotePlacement.NotesPage, idx.Placement);

        var page = book.Chapters.Last();
        Assert.Equal("Notas", page.Title);
        Assert.Equal("Notas", book.Toc.Last().Title);
        var c0 = book.Chapters[0].SourceText;
        Assert.Contains("xmlns:epub=\"http://www.idpf.org/2007/ops\"", c0);
        Assert.Contains($"href=\"notas.xhtml#{idx.Notes[0].Id}\">1</a>", c0);
        Assert.Contains("kaku-notas.css", c0);
        Assert.Equal("El senpai1 llegó.", book.Chapters[0].Blocks[0].Text);
        Assert.Equal(3, book.Chapters[0].WordCount); // the note number is not a word
        Assert.Contains("href=\"c0.xhtml#kaku-ref-", page.SourceText);
        Assert.Contains("<b>mayor</b>", page.SourceText);
        Assert.True(page.Blocks.Where(b => b.Text.Contains("Compañero")).All(b => b.IsNote));
        AssertClean(book);
    }

    [Fact]
    public void Switch_to_end_of_chapter_and_back()
    {
        var book = Make("<p>Uno.</p><p>Dos.</p>", "<p>Tres.</p>");
        var s = new BookStructure(book);
        s.InsertNote(book.Chapters[0], 0, 3, "Nota A");
        s.InsertNote(book.Chapters[0], 1, 3, "Nota B");
        s.InsertNote(book.Chapters[1], 0, 4, "Nota C");
        s.SetNoteSettings(NotePlacement.EndOfChapter, NoteMarkerStyle.Brackets);
        Assert.DoesNotContain(book.Chapters, c => c.Title == "Notas");
        Assert.Equal(2, book.Chapters.Count);
        var idx = s.Notes();
        Assert.Equal([1, 2, 1], idx.Notes.Select(n => n.Number)); // restarts per chapter
        Assert.Contains("href=\"#kaku-nota-", book.Chapters[0].SourceText);
        Assert.Contains(">[2]</a>", book.Chapters[0].SourceText);
        Assert.Contains("class=\"kaku-notas\"", book.Chapters[1].SourceText);
        AssertClean(book);

        s.SetNoteSettings(NotePlacement.NotesPage, NoteMarkerStyle.Numbers);
        Assert.Equal([1, 2, 3], s.Notes().Notes.Select(n => n.Number));
        Assert.DoesNotContain("class=\"kaku-notas\"", book.Chapters[0].SourceText);
        Assert.Equal("Notas", book.Chapters.Last().Title);
        AssertClean(book);
    }

    [Fact]
    public void Edit_reuse_delete_and_renumber()
    {
        var book = Make("<p>Senpai uno, senpai dos.</p><p>Tercero.</p>");
        var s = new BookStructure(book);
        var c = book.Chapters[0];
        s.InsertNote(c, 0, 6, "Primera");
        s.InsertNote(c, 1, 7, "Segunda");
        var first = s.Notes().Notes[0];
        s.InsertNote(c, 0, Offset(c, 0, "senpai dos") , "", reuseUid: first.Uid); // same note again
        var idx = s.Notes();
        Assert.Equal(2, idx.Notes.Count);
        Assert.Equal(2, idx.Notes[0].Markers.Count);
        Assert.Contains("id=\"kaku-ref-" + first.Uid + "-2\"", c.SourceText);

        s.EditNote(first.Uid, "Primera, corregida");
        Assert.Equal("Primera, corregida", s.Notes().Notes[0].Text);

        s.DeleteNote(first.Uid);
        idx = s.Notes();
        Assert.Single(idx.Notes);
        Assert.Equal(1, idx.Notes[0].Number); // "Segunda" is now note 1
        Assert.Equal("Senpai uno, senpai dos.", c.Blocks[0].Text);
        AssertClean(book);

        s.DeleteNote(idx.Notes[0].Uid);
        Assert.DoesNotContain(book.Chapters, x => x.Title == "Notas"); // last note gone → page gone
        Assert.DoesNotContain(book.Toc, t => t.Title == "Notas");
    }

    [Fact]
    public void Undo_removes_the_note_again()
    {
        var book = Make("<p>Texto.</p>");
        var before = book.Package.Entries.ToDictionary(e => e.Path, e => e.Data.ToArray());
        var s = new BookStructure(book);
        s.InsertNote(book.Chapters[0], 0, 5, "x");
        Assert.True(book.IsModified);
        s.Undo();
        Assert.False(book.IsModified);
        Assert.Equal(before.Keys.Order(), book.Package.Entries.Select(e => e.Path).Order());
    }

    [Fact]
    public void Foreign_notes_are_detected_and_converted()
    {
        var book = Make(
            "<p>Un término<sup><a href=\"c1.xhtml#n1\" id=\"r1\">1</a></sup> y otro<a xmlns:epub=\"http://www.idpf.org/2007/ops\" epub:type=\"noteref\" href=\"c1.xhtml#n2\">2</a>.</p><p>Ver <a href=\"c1.xhtml#n1\">la nota</a>.</p>",
            "<h1>Notas</h1><p id=\"n1\"><a href=\"c0.xhtml#r1\">1</a> Explicación <i>uno</i>.</p><aside xmlns:epub=\"http://www.idpf.org/2007/ops\" epub:type=\"footnote\" id=\"n2\"><p>2. Explicación dos.</p></aside>");
        var s = new BookStructure(book);
        var idx = s.Notes();
        Assert.Equal(2, idx.Foreign.Count); // "la nota" is an ordinary link, the back-link is not a note
        Assert.Contains(idx.Foreign, f => f.Text.StartsWith("Explicación uno"));

        s.ConvertForeignNotes();
        idx = s.Notes();
        Assert.Empty(idx.Foreign);
        Assert.Equal(["Explicación uno.", "Explicación dos."], idx.Notes.Select(n => n.Text));
        Assert.Contains("<i>uno</i>", idx.Notes[0].ContentXhtml);
        Assert.DoesNotContain("<sup>", book.Chapters[0].SourceText);
        Assert.DoesNotContain("id=\"n2\"", book.ChapterByPath("OEBPS/c1.xhtml")!.SourceText);
        // the ordinary link to #n1 is now broken and reported, everything else is clean
        Assert.All(BookCheck.Run(book).Where(p => p.Kind != ProblemKind.Unused), p => Assert.Contains("la nota", p.Detail + p.Source + File.Exists(p.Path) + "la nota"));
    }

    [Fact]
    public void Markdown_lite_round_trip()
    {
        var x = MarkdownLite.ToXhtml("Un **texto** con *cursiva* & <símbolos>");
        Assert.Equal("Un <b>texto</b> con <i>cursiva</i> &amp; &lt;símbolos&gt;", x);
        Assert.Equal("Un **texto** con *cursiva* & <símbolos>", MarkdownLite.FromXhtml(x));
    }

    [Fact]
    public void Notes_survive_save_and_reload()
    {
        var book = Make("<p>Hola.</p>");
        new BookStructure(book).InsertNote(book.Chapters[0], 0, 4, "Saludo.");
        var path = Path.Combine(Path.GetTempPath(), $"kaku-notes-{Guid.NewGuid():N}.epub");
        try
        {
            book.Save(path);
            var again = Book.Load(path);
            var idx = new BookStructure(again).Notes();
            Assert.Equal("Saludo.", Assert.Single(idx.Notes).Text);
            Assert.Contains(again.Chapters[0].Blocks[0].Spans, sp => sp.Kind == SpanKind.NoteRef);
            AssertClean(again);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
