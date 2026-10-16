using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Tests;

public class TextExtractorTests
{
    private static List<ContentBlock> Extract(string body) =>
        TextExtractor.Extract(XmlLoader.Parse(
            $"<?xml version=\"1.0\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>t</title></head><body>{body}</body></html>"),
            "OEBPS/Text/ch1.xhtml").Blocks;

    [Fact]
    public void Paragraphs_collapse_whitespace_and_drop_tags()
    {
        var b = Extract("<div class=\"main\">\n<p>  Hola,\n   <b>mundo</b>  cruel. </p>\n<p>Otro</p></div>");
        Assert.Equal(2, b.Count);
        Assert.Equal("Hola, mundo cruel.", b[0].Text);
        Assert.Contains(b[0].Spans, s => s.Kind == SpanKind.Bold && b[0].Text.Substring(s.Start, s.Length) == "mundo");
        Assert.Equal(3, b[0].WordCount);
        Assert.Equal(3, b[1].WordStart);
    }

    [Fact]
    public void Ruby_reading_is_excluded_from_text()
    {
        var b = Extract("<p><ruby>後東<rt>ことう</rt></ruby>さん</p>");
        Assert.Equal("後東さん", b[0].Text);
        var ruby = Assert.Single(b[0].Spans, s => s.Kind == SpanKind.Ruby);
        Assert.Equal("ことう", ruby.Data);
        Assert.Equal(4, b[0].WordCount); // each CJK/kana char is one word
    }

    [Fact]
    public void Images_and_breaks_are_placeholders_not_words()
    {
        var b = Extract("<p>　<span class=\"koboSpan\"><img src=\"../Images/suzuki.png\" alt=\"\"/></span></p><p><br/></p>");
        Assert.Equal(BlockKind.Image, b[0].Kind);
        Assert.Equal(0, b[0].WordCount);
        Assert.Contains(b[0].Spans, s => s.Kind == SpanKind.Image && s.Data == "OEBPS/Images/suzuki.png");
        Assert.Equal(TextExtractor.LineBreakChar.ToString(), b[1].Text);
    }

    [Fact]
    public void Svg_cover_image_is_detected()
    {
        var b = Extract("<div><svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><image xlink:href=\"../image/cover.jpg\"/></svg></div>");
        var block = Assert.Single(b);
        Assert.Equal(BlockKind.Image, block.Kind);
        Assert.Contains(block.Spans, s => s.Data == "OEBPS/image/cover.jpg");
    }

    [Fact]
    public void Headings_notes_and_loose_text()
    {
        var b = Extract("<h2 id=\"c1\">Capítulo uno</h2><div>texto suelto<p>dentro</p></div>" +
                        "<p>Hola<a epub:type=\"noteref\" href=\"#n1\">1</a></p><aside epub:type=\"footnote\" id=\"n1\"><p>Nota larga aquí</p></aside>");
        Assert.Equal(BlockKind.Heading, b[0].Kind);
        Assert.Equal(2, b[0].HeadingLevel);
        Assert.Equal("texto suelto", b[1].Text);
        Assert.Equal("#text", b[1].Tag);
        Assert.Equal("dentro", b[2].Text);
        Assert.Contains(b[3].Spans, s => s.Kind == SpanKind.NoteRef);
        Assert.True(b[4].IsNote);
        Assert.False(b[3].IsNote);
    }

    [Fact]
    public void Html_entities_are_accepted()
    {
        var b = Extract("<p>a&nbsp;b &mdash; c &amp; d &unknownthing;</p>");
        Assert.Equal("a b — c & d &unknownthing;", b[0].Text);
    }

    [Fact]
    public void Words_handle_apostrophes_and_cjk()
    {
        Assert.Equal(3, WordCounter.Count("d'Artagnan dijo «¡hola!»"));
        Assert.Equal(5, WordCounter.Count("東京abc 京都"));
        Assert.Equal(4, WordCounter.OffsetOfWord("uno dos tres", 1));
        Assert.Equal(2, WordCounter.CountBefore("uno dos tres", 5));
    }

    [Fact]
    public void Fold_removes_accents_but_keeps_kana_voicing()
    {
        Assert.Equal("koto arbol", TextNormalizer.Fold("Kōtō ÁRBOL"));
        Assert.Equal("が", TextNormalizer.Fold("が"));
    }

    [Theory]
    [InlineData("OEBPS/Text/ch1.xhtml", "../Images/a.png", "OEBPS/Images/a.png")]
    [InlineData("OEBPS/content.opf", "Text/ch%201.xhtml#x", "OEBPS/Text/ch 1.xhtml")]
    [InlineData("ch.xhtml", "./img/a.jpg", "img/a.jpg")]
    public void Paths_resolve(string baseFile, string href, string expected) =>
        Assert.Equal(expected, EpubPath.Resolve(baseFile, href));
}
