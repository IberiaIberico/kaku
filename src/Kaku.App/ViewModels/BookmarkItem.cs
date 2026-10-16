using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;

namespace Kaku.App.ViewModels;

/// <summary>A row in the "Marcadores" tab.</summary>
public sealed class BookmarkItem(Book book, Chapter chapter, BookmarkData data)
{
    public BookmarkData Data { get; } = data;
    public Chapter Chapter { get; } = chapter;
    public int Block => Math.Clamp(Data.Block, 0, Math.Max(0, Chapter.Blocks.Count - 1));
    public TextLocation Location => new(Chapter.Index, Block, 0);
    public string Header => T.F("Bookmark_Header", Chapter.Index + 1, Chapter.Title, Block + 1);
    public string PositionText => T.F("Bookmark_Position", T.N1(book.PercentAt(Location)));
    public string Snippet => Chapter.Blocks.Count > Block ? Shorten(Chapter.Blocks[Block].Text) : Data.Snippet;

    private static string Shorten(string s) => s.Length <= 110 ? s : s[..110] + "…";
}
