using System.Globalization;
using System.Text;

namespace Kaku.Core.Text;

/// <summary>
/// Word segmentation used for positions: a word is a run of letters/digits (marks and inner
/// apostrophes included). Each CJK ideograph or kana counts as one word.
/// </summary>
public static class WordCounter
{
    public static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ'   // hiragana, katakana
          or >= '㐀' and <= '䶿'   // CJK ext A
          or >= '一' and <= '鿿'   // CJK unified
          or >= '豈' and <= '﫿'   // compatibility ideographs
          or >= 'ｦ' and <= 'ﾟ';  // half-width katakana

    private static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    /// <summary>Enumerates (start, length) of each word.</summary>
    public static IEnumerable<(int Start, int Length)> Words(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (IsCjk(c)) { yield return (i, 1); i++; continue; }
            if (!IsWordChar(c)) { i++; continue; }
            int start = i;
            while (i < n)
            {
                char d = text[i];
                if (IsCjk(d)) break;
                if (IsWordChar(d)) { i++; continue; }
                // apostrophe between letters keeps the word together (d'Artagnan)
                if (d is '\'' or '’' && i + 1 < n && i > start && IsWordChar(text[i + 1]) && !IsCjk(text[i + 1])) { i++; continue; }
                break;
            }
            yield return (start, i - start);
        }
    }

    public static int Count(string text)
    {
        int count = 0;
        foreach (var _ in Words(text)) count++;
        return count;
    }

    /// <summary>Number of words that start before <paramref name="offset"/>.</summary>
    public static int CountBefore(string text, int offset)
    {
        int count = 0;
        foreach (var (s, _) in Words(text))
        {
            if (s >= offset) break;
            count++;
        }
        return count;
    }

    /// <summary>Character offset where the n-th (0-based) word starts; text length if out of range.</summary>
    public static int OffsetOfWord(string text, int n)
    {
        int i = 0;
        foreach (var (s, _) in Words(text))
        {
            if (i++ == n) return s;
        }
        return text.Length;
    }

    /// <summary>Copy of <paramref name="text"/> with the given ranges replaced by spaces.</summary>
    public static string Blank(string text, IEnumerable<InlineSpan> placeholders)
    {
        StringBuilder? sb = null;
        foreach (var p in placeholders)
        {
            sb ??= new StringBuilder(text);
            for (int i = p.Start; i < p.End && i < sb.Length; i++) sb[i] = ' ';
        }
        return sb?.ToString() ?? text;
    }
}
