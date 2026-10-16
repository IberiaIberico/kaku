using System.IO.Compression;
using System.Text;
using Kaku.Core.Editing;
using Kaku.Core.Epub;
using Kaku.Core.Structure;
using Xunit.Abstractions;

namespace Kaku.Core.Tests;

public class StructureTests(ITestOutputHelper output)
{
    private const string Xhtml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" class=\"hltr\">\n<head>\n<title>{0}</title>\n<link rel=\"stylesheet\" type=\"text/css\" href=\"../Styles/s.css\"/>\n</head>\n<body class=\"p-text\">\n{1}\n</body>\n</html>\n";

    /// <summary>EPUB 3 book with nav + NCX, three chapters linking to each other.</summary>
    private static Book Make()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaku-struct-{Guid.NewGuid():N}.epub");
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
            Add("OEBPS/content.opf", """
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="id">x</dc:identifier>
                    <dc:title>Libro</dc:title>
                    <dc:creator id="c1">Autora</dc:creator>
                    <meta refines="#c1" property="file-as">Autora, X</meta>
                    <dc:language>es</dc:language>
                    <meta property="dcterms:modified">2020-01-01T00:00:00Z</meta>
                  </metadata>
                  <manifest>
                    <item id="nav" href="Text/nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                    <item id="css" href="Styles/s.css" media-type="text/css"/>
                    <item id="img" href="Images/a.png" media-type="image/png"/>
                    <item id="c1" href="Text/c1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="Text/c2.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c3" href="Text/c3.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine toc="ncx">
                    <itemref idref="c1"/>
                    <itemref idref="c2"/>
                    <itemref idref="c3"/>
                  </spine>
                </package>
                """);
            Add("OEBPS/toc.ncx", """
                <?xml version="1.0" encoding="UTF-8"?>
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
                <head><meta name="dtb:depth" content="1"/></head>
                <docTitle><text>Libro</text></docTitle>
                <navMap>
                  <navPoint id="n1" playOrder="1"><navLabel><text>Uno</text></navLabel><content src="Text/c1.xhtml"/></navPoint>
                  <navPoint id="n2" playOrder="2"><navLabel><text>Dos</text></navLabel><content src="Text/c2.xhtml"/></navPoint>
                  <navPoint id="n3" playOrder="3"><navLabel><text>Tres</text></navLabel><content src="Text/c3.xhtml#t3"/></navPoint>
                </navMap>
                </ncx>
                """);
            Add("OEBPS/Text/nav.xhtml", string.Format(Xhtml, "Índice", """
                <nav epub:type="toc" id="toc">
                  <h1>Índice</h1>
                  <ol>
                    <li><a href="c1.xhtml">Uno</a></li>
                    <li><a href="c2.xhtml">Dos</a></li>
                    <li><a href="c3.xhtml#t3">Tres</a></li>
                  </ol>
                </nav>
                """));
            Add("OEBPS/Styles/s.css", "p { margin: 0; }\n.fondo { background: url(../Images/a.png); }\n");
            using (var png = zip.CreateEntry("OEBPS/Images/a.png").Open()) png.Write([0x89, 0x50, 0x4E, 0x47]);
            Add("OEBPS/Text/c1.xhtml", string.Format(Xhtml, "Uno", "<h1>Uno</h1>\n<p>Primero. Ver <a href=\"c3.xhtml#t3\">el tres</a>.</p>"));
            Add("OEBPS/Text/c2.xhtml", string.Format(Xhtml, "Dos", "<h1>Dos</h1>\n<div class=\"main\" id=\"m2\">\n<p id=\"a\">Antes del corte.</p>\n<p id=\"b\">Después <a href=\"#a\">volver</a>.</p>\n<p>Final <img src=\"../Images/a.png\" alt=\"\"/></p>\n</div>"));
            Add("OEBPS/Text/c3.xhtml", string.Format(Xhtml, "Tres", "<h1 id=\"t3\">Tres</h1>\n<p id=\"a\">Otra a. <a href=\"c2.xhtml#b\">ir al b</a> y <a href=\"c2.xhtml\">al dos</a>.</p>"));
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

    private static Book SaveAndReload(Book book)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaku-struct-{Guid.NewGuid():N}.epub");
        try
        {
            book.Save(path);
            return Book.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthetic_book_starts_clean()
    {
        var book = Make();
        AssertClean(book);
        Assert.Equal(3, book.Toc.Count);
        Assert.False(book.IsModified);
    }

    [Fact]
    public void Add_page_with_toc_entry_and_neighbour_styles()
    {
        var book = Make();
        var s = new BookStructure(book);
        var ch = s.AddPage("Nota del traductor", BookStructure.TemplateBody(PageTemplate.TranslatorNote, "Nota del traductor"), 1, addToToc: true);
        Assert.Equal("OEBPS/Text/nota-del-traductor.xhtml", ch.Focus);
        Assert.Equal(4, book.Chapters.Count);
        Assert.Equal(ch.Focus, book.Chapters[1].Path);
        Assert.Equal(["Uno", "Nota del traductor", "Dos", "Tres"], book.Toc.Select(t => t.Title));
        var text = book.Chapters[1].SourceText;
        Assert.Contains("href=\"../Styles/s.css\"", text);
        Assert.Contains("class=\"p-text\"", text);
        Assert.True(book.IsModified);
        AssertClean(book);

        var again = SaveAndReload(book);
        Assert.Equal(4, again.Toc.Count);
        Assert.Equal("Nota del traductor", again.Chapters[1].Title);
        Assert.Contains("nota-del-traductor.xhtml", Encoding.UTF8.GetString(again.Package.Read("OEBPS/toc.ncx")!));
    }

    [Fact]
    public void Delete_removes_manifest_spine_and_toc()
    {
        var book = Make();
        var s = new BookStructure(book);
        Assert.Single(s.UsagesOf("OEBPS/Text/c2.xhtml").Where(u => u.Source.EndsWith("c3.xhtml") && u.Fragment == "b"));
        s.DeleteFiles(["OEBPS/Text/c2.xhtml"]);
        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal(["Uno", "Tres"], book.Toc.Select(t => t.Title));
        Assert.False(book.Package.Exists("OEBPS/Text/c2.xhtml"));
        // the links from c3 to the deleted page are now reported
        Assert.Contains(BookCheck.Run(book), p => p.Kind == ProblemKind.BrokenLink && p.Source!.EndsWith("c3.xhtml"));
    }

    [Fact]
    public void Rename_rewrites_every_link()
    {
        var book = Make();
        var s = new BookStructure(book);
        var c = s.Rename("OEBPS/Text/c3.xhtml", "capitulo-tres");
        Assert.Equal("OEBPS/Text/capitulo-tres.xhtml", c.Focus);
        Assert.Contains("capitulo-tres.xhtml#t3", book.Chapters[0].SourceText);
        Assert.Equal("OEBPS/Text/capitulo-tres.xhtml", book.Toc[2].Path);
        Assert.Equal("t3", book.Toc[2].Fragment);
        AssertClean(book);

        s.Rename("Images/a.png".Insert(0, "OEBPS/"), "foto.png");
        Assert.Contains("url(../Images/foto.png)", Encoding.UTF8.GetString(book.Package.Read("OEBPS/Styles/s.css")!));
        Assert.Contains("../Images/foto.png", book.ChapterByPath("OEBPS/Text/c2.xhtml")!.SourceText);
        AssertClean(book);
    }

    [Fact]
    public void Move_changes_reading_order()
    {
        var book = Make();
        var s = new BookStructure(book);
        s.Move("OEBPS/Text/c3.xhtml", "OEBPS/Text/c1.xhtml");
        Assert.Equal(["c3.xhtml", "c1.xhtml", "c2.xhtml"], book.Chapters.Select(c => EpubPath.FileName(c.Path)));
        s.Move("OEBPS/Text/c3.xhtml", null);
        Assert.Equal(["c1.xhtml", "c2.xhtml", "c3.xhtml"], book.Chapters.Select(c => EpubPath.FileName(c.Path)));
        s.Move("OEBPS/Text/c3.xhtml", "OEBPS/Text/c1.xhtml");
        s.SortTocByReadingOrder();
        Assert.Equal(["Tres", "Uno", "Dos"], book.Toc.Select(t => t.Title));
    }

    [Fact]
    public void Split_moves_ids_and_fixes_links_then_merge_restores()
    {
        var book = Make();
        var s = new BookStructure(book);
        var c2 = book.ChapterByPath("OEBPS/Text/c2.xhtml")!;
        int b = c2.Blocks.FindIndex(x => x.Text.StartsWith("Después"));
        var words = book.TotalWords;
        var change = s.Split(c2, b);
        Assert.Equal("OEBPS/Text/c2-2.xhtml", change.Focus);
        var c2b = book.ChapterByPath(change.Focus!)!;
        Assert.Equal(4, book.Chapters.Count);
        Assert.Equal(words, book.TotalWords);
        Assert.StartsWith("Después", c2b.Blocks[0].Text);
        // c3 linked to c2#b, which moved; c2-2 linked to #a, which stayed in c2
        Assert.Contains("c2-2.xhtml#b", book.ChapterByPath("OEBPS/Text/c3.xhtml")!.SourceText);
        Assert.Contains("href=\"c2.xhtml#a\"", c2b.SourceText);
        Assert.DoesNotContain("id=\"m2\"", c2b.SourceText); // the copied container lost its id
        Assert.Contains("class=\"main\"", c2b.SourceText);
        AssertClean(book);

        s.MergeWithNext(book.ChapterByPath("OEBPS/Text/c2.xhtml")!);
        Assert.Equal(3, book.Chapters.Count);
        Assert.Equal(words, book.TotalWords);
        Assert.Contains("c2.xhtml#b", book.ChapterByPath("OEBPS/Text/c3.xhtml")!.SourceText);
        AssertClean(book);
    }

    [Fact]
    public void Merge_renames_clashing_ids_and_redirects_file_links()
    {
        var book = Make();
        var s = new BookStructure(book);
        s.MergeWithNext(book.ChapterByPath("OEBPS/Text/c2.xhtml")!); // c3 has id "a" too
        var c2 = book.ChapterByPath("OEBPS/Text/c2.xhtml")!;
        Assert.Contains("id=\"a-2\"", c2.SourceText);
        Assert.Equal("t3", book.Toc[2].Fragment);
        Assert.Equal("OEBPS/Text/c2.xhtml", book.Toc[2].Path);
        Assert.Contains("c2.xhtml#t3", book.Chapters[0].SourceText);
        AssertClean(book);
    }

    [Fact]
    public void Undo_and_redo_restore_the_package()
    {
        var book = Make();
        var before = book.Package.Entries.ToDictionary(e => e.Path, e => e.Data.ToArray());
        var s = new BookStructure(book);
        s.Split(book.Chapters[1], 2);
        s.Rename("OEBPS/Text/c1.xhtml", "uno.xhtml");
        Assert.True(book.IsModified);
        var inv = s.Undo()!;
        Assert.Equal("OEBPS/Text/c1.xhtml", inv.Renamed["OEBPS/Text/uno.xhtml"]);
        s.Undo();
        Assert.False(book.IsModified);
        Assert.Equal(before.Keys.Order(), book.Package.Entries.Select(e => e.Path).Order());
        foreach (var e in book.Package.Entries) Assert.Equal(before[e.Path], e.Data);
        s.Redo();
        Assert.Equal(4, book.Chapters.Count);
    }

    [Fact]
    public void Open_editors_keep_their_chapter_object_across_rebuilds()
    {
        var book = Make();
        var c3 = book.ChapterByPath("OEBPS/Text/c3.xhtml")!;
        new ChapterEditor(book, c3).ApplyTextChanges(1, [new TextChange(0, 0, "¡Editado! ")]);
        var s = new BookStructure(book);
        s.Rename(c3.Path, "tres.xhtml");
        Assert.Same(c3, book.ChapterByPath("OEBPS/Text/tres.xhtml"));
        Assert.StartsWith("¡Editado!", c3.Blocks[1].Text);
        s.Move(c3.Path, book.Chapters[0].Path);
        Assert.Equal(0, c3.Index);
    }

    [Fact]
    public void Generate_and_write_toc_adds_heading_ids()
    {
        var book = Make();
        var s = new BookStructure(book);
        new ChapterEditor(book, book.Chapters[1]).SetSource(book.Chapters[1].SourceText.Replace("<p id=\"b\">", "<h2>Sección</h2>\n<p id=\"b\">"));
        var drafts = s.GenerateToc();
        Assert.Equal(["Uno", "Dos", "Sección", "Tres"], drafts.Select(d => d.Title));
        Assert.Equal(1, drafts[2].Level);
        s.SetToc(drafts);
        Assert.Equal(4, book.Toc.Count);
        Assert.Equal("kaku-toc-1", book.Toc[2].Fragment);
        Assert.Contains("<h2 id=\"kaku-toc-1\">Sección</h2>", book.Chapters[1].SourceText);
        var ncx = Encoding.UTF8.GetString(book.Package.Read("OEBPS/toc.ncx")!);
        Assert.Contains("c2.xhtml#kaku-toc-1", ncx);
        Assert.Contains("content=\"2\"", ncx); // dtb:depth
        AssertClean(book);
        Assert.Equal(4, SaveAndReload(book).Toc.Count);
    }

    [Fact]
    public void Metadata_round_trip_keeps_refines()
    {
        var book = Make();
        var s = new BookStructure(book);
        var m = s.GetMetadata();
        Assert.Equal(["Autora"], m.Creators);
        s.SetMetadata(m with { Title = "Título nuevo", Creators = ["Autora", "Traductor"], Series = "Saga", SeriesIndex = "2", Description = "Una descripción." });
        Assert.Equal("Título nuevo", book.Title);
        var again = s.GetMetadata();
        Assert.Equal(["Autora", "Traductor"], again.Creators);
        Assert.Equal("Saga", again.Series);
        Assert.Equal("2", again.SeriesIndex);
        var opf = Encoding.UTF8.GetString(book.Package.Read("OEBPS/content.opf")!);
        Assert.Contains("file-as", opf);          // unchanged creator keeps its refines
        Assert.Contains("calibre:series", opf);
        Assert.DoesNotContain("2020-01-01", opf); // dcterms:modified updated
        Assert.Equal("Título nuevo", SaveAndReload(book).Title);
    }

    [Fact]
    public void Add_replace_cover_and_insert_image()
    {
        var book = Make();
        var s = new BookStructure(book);
        var add = s.AddFile("Mi Foto.JPG", [1, 2, 3]);
        Assert.Equal("OEBPS/Images/mi-foto.jpg", add.Focus);
        s.SetCover(add.Focus!);
        Assert.Equal(add.Focus, book.Opf.CoverImagePath);
        var ed = new ChapterEditor(book, book.Chapters[0]);
        Assert.True(ed.InsertImageParagraph(0, add.Focus!));
        Assert.Contains("<img src=\"../Images/mi-foto.jpg\" alt=\"\"/>", book.Chapters[0].SourceText);
        var r = s.ReplaceFile(add.Focus!, "otra.png", [4, 5]);
        Assert.Equal("OEBPS/Images/mi-foto.png", r.Focus);
        Assert.Contains("../Images/mi-foto.png", book.Chapters[0].SourceText);
        Assert.Equal("image/png", book.Opf.ItemByPath(r.Focus!)!.MediaType);
        AssertClean(book);
    }

    [Fact]
    public void Import_markdown_and_text()
    {
        var (title, body) = BookStructure.ImportBody("nota.md", "# Nota\n\nUna **frase** con *cursiva*\nque sigue.\n\nOtro párrafo & más.");
        Assert.Equal("Nota", title);
        Assert.Equal("<h1>Nota</h1>\n<p>Una <b>frase</b> con <i>cursiva</i> que sigue.</p>\n<p>Otro párrafo &amp; más.</p>", body);
        var (_, txt) = BookStructure.ImportBody("a.txt", "uno\ndos");
        Assert.Equal("<p>uno</p>\n<p>dos</p>", txt);
    }

    [Fact]
    public void Unused_and_unlisted_files_are_reported()
    {
        var book = Make();
        book.Package.Put("OEBPS/Images/suelta.png", [1]);
        new BookStructure(book).AddFile("sin-uso.png", [1]);
        var problems = BookCheck.Run(book);
        Assert.Contains(problems, p => p.Kind == ProblemKind.NotInManifest && p.Path.EndsWith("suelta.png"));
        Assert.Contains(problems, p => p.Kind == ProblemKind.Unused && p.Path.EndsWith("sin-uso.png"));
    }

    [Fact]
    public void Real_books_take_notes_without_new_problems()
    {
        foreach (var path in RealBooksTests.BookPaths())
        {
            var book = Book.Load(path);
            int baseline = BookCheck.Run(book).Count(p => p.Kind != ProblemKind.Unused);
            var s = new BookStructure(book);
            var foreign = s.Notes().Foreign.Count;
            var c = book.Chapters.Where(x => x.WordCount > 500 && x.ParseError == null).MaxBy(x => x.WordCount)!;
            var b = c.Blocks.FindIndex(x => x.WordCount > 10 && !x.IsAnonymous);
            var words = book.TotalWords;
            var w = Kaku.Core.Text.WordCounter.Words(c.Blocks[b].WordText).Skip(1).First(); // after the second word
            s.InsertNote(c, b, w.Start + w.Length, "Nota de prueba.");
            s.InsertNote(c, b + 1, 0, "Otra nota.");
            s.SetNoteSettings(Kaku.Core.Notes.NotePlacement.EndOfChapter, Kaku.Core.Notes.NoteMarkerStyle.Numbers);
            s.SetNoteSettings(Kaku.Core.Notes.NotePlacement.NotesPage, Kaku.Core.Notes.NoteMarkerStyle.Numbers);
            int after = BookCheck.Run(book).Count(p => p.Kind != ProblemKind.Unused);
            output.WriteLine($"{Path.GetFileName(path)}: foreign notes {foreign}, problems {baseline} → {after}");
            Assert.Equal(2, s.Notes().Notes.Count);
            Assert.True(after <= baseline, string.Join("\n", BookCheck.Run(book).Take(5)));
            Assert.Equal(words + 1, book.TotalWords); // + the "Notas" heading
        }
    }

    [Fact]
    public void Real_books_split_merge_rename_keep_links_valid()
    {
        foreach (var path in RealBooksTests.BookPaths())
        {
            var book = Book.Load(path);
            int baseline = BookCheck.Run(book).Count(p => p.Kind is ProblemKind.BrokenLink or ProblemKind.MissingFragment);
            var words = book.TotalWords;
            var s = new BookStructure(book);
            var c = book.Chapters.Where(x => x.Blocks.Count > 20 && x.ParseError == null).MaxBy(x => x.Blocks.Count)!;
            int at = Enumerable.Range(c.Blocks.Count / 2, c.Blocks.Count / 2).First(i => !c.Blocks[i].IsAnonymous && !c.Blocks[i].IsNote);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s.Split(c, at);
            var tSplit = sw.ElapsedMilliseconds;
            Assert.Equal(words, book.TotalWords);
            s.MergeWithNext(book.ChapterByPath(c.Path)!);
            Assert.Equal(words, book.TotalWords);
            s.Rename(c.Path, "renombrado.xhtml");
            int after = BookCheck.Run(book).Count(p => p.Kind is ProblemKind.BrokenLink or ProblemKind.MissingFragment);
            output.WriteLine($"{Path.GetFileName(path)}: split {tSplit} ms, broken links {baseline} → {after}");
            Assert.True(after <= baseline, $"{Path.GetFileName(path)}: {baseline} → {after}\n" + string.Join("\n", BookCheck.Run(book).Take(5)));
            var again = SaveAndReload(book);
            Assert.Equal(book.Chapters.Count, again.Chapters.Count);
            Assert.Equal(words, again.TotalWords);
        }
    }
}
