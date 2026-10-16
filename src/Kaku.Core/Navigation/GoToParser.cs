using System.Globalization;
using System.Text.RegularExpressions;
using Kaku.Core.Text;

namespace Kaku.Core.Navigation;

public enum GoToKind { Percent, Word, Chapter, ChapterParagraph, ChapterTitle }

public sealed record GoToTarget(GoToKind Kind, TextLocation Location);

/// <summary>
/// Parses the "Ir a" box: <c>45%</c>, <c>45,8%</c>, <c>w45210</c> / <c>palabra 45.210</c>,
/// <c>3</c> (chapter 3), <c>3:12</c> (chapter 3, paragraph 12) or part of a chapter title.
/// Chapter and paragraph numbers are 1-based, in reading order.
/// </summary>
public static partial class GoToParser
{
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*%$")]
    private static partial Regex PercentRe();

    [GeneratedRegex(@"^(?:w|p|palabra)\s*([\d.\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WordRe();

    [GeneratedRegex(@"^(\d+)\s*(?::\s*(\d+))?$")]
    private static partial Regex ChapterRe();

    public static GoToTarget? Parse(Book book, string input)
    {
        var s = input.Trim();
        if (s.Length == 0 || book.Chapters.Count == 0) return null;

        var m = PercentRe().Match(s);
        if (m.Success)
        {
            var pct = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            return new GoToTarget(GoToKind.Percent, book.LocationOfPercent(pct));
        }

        m = WordRe().Match(s);
        if (m.Success)
        {
            var digits = new string(m.Groups[1].Value.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, out var w) && w > 0)
                return new GoToTarget(GoToKind.Word, book.LocationOfWord(w - 1));
        }

        m = ChapterRe().Match(s);
        if (m.Success)
        {
            int ch = int.Parse(m.Groups[1].Value) - 1;
            if (ch < 0 || ch >= book.Chapters.Count) return null;
            if (!m.Groups[2].Success) return new GoToTarget(GoToKind.Chapter, new TextLocation(ch, 0, 0));
            int blocks = book.Chapters[ch].Blocks.Count;
            int p = int.Parse(m.Groups[2].Value) - 1;
            if (p < 0 || p >= blocks) return null;
            return new GoToTarget(GoToKind.ChapterParagraph, new TextLocation(ch, p, 0));
        }

        // part of a chapter title
        var best = FuzzyFilter.Best(s, book.Chapters, c => c.Title);
        return best == null ? null : new GoToTarget(GoToKind.ChapterTitle, new TextLocation(best.Index, 0, 0));
    }
}
