using System.Globalization;
using System.Text;

namespace Kaku.Core.Text;

/// <summary>A symbol used in the book and how many times.</summary>
public sealed record SymbolCount(string Symbol, int Count);

/// <summary>Counts the "special" characters of a book: punctuation, symbols, letters with macrons, special spaces.</summary>
public static class SymbolStats
{
    /// <summary>Characters anyone can type directly: not worth listing.</summary>
    private const string Typable = ".,;:()";

    public static bool IsInteresting(Rune r)
    {
        if (r.Value < 128)
            return !Rune.IsLetterOrDigit(r) && !Rune.IsWhiteSpace(r) && !Typable.Contains((char)r.Value) && r.Value >= 32;
        if (r.Value is 0x00A0 or 0x202F or 0x2009 or 0x200A or 0x2007 or 0x3000) return true; // special spaces
        if (r.Value is >= 0x3040 and <= 0x30FF && r.Value is not (0x30FB or 0x30FC)) return false; // kana (but ・ and ー count)
        if (r.Value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF) return false;  // kanji
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark) return false;
        if (Rune.IsLetter(r))
        {
            // ordinary Spanish letters are not "symbols"; other accented letters (ō, ū, ç…) are
            return "áéíóúüñÁÉÍÓÚÜÑ".IndexOf((char)r.Value) < 0;
        }
        return !Rune.IsDigit(r);
    }

    public static List<SymbolCount> Count(Book book)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in book.Chapters)
            foreach (var b in c.Blocks)
            {
                // placeholders ([▣ imagen], ↵) and note numbers are blanked in WordText; skip them too
                var text = b.WordText;
                foreach (var r in text.EnumerateRunes())
                    if (IsInteresting(r))
                    {
                        var s = r.ToString();
                        counts[s] = counts.GetValueOrDefault(s) + 1;
                    }
            }
        return counts.Select(kv => new SymbolCount(kv.Key, kv.Value)).OrderByDescending(s => s.Count).ThenBy(s => s.Symbol, StringComparer.Ordinal).ToList();
    }
}
