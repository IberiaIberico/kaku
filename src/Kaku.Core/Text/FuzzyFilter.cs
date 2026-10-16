namespace Kaku.Core.Text;

/// <summary>Scoring for quick-open style filtering (command palette, go-to by title). Accent/case-insensitive.</summary>
public static class FuzzyFilter
{
    /// <summary>Score of <paramref name="candidate"/> for the query, or -1 if it does not match. Higher is better.</summary>
    public static int Score(string query, string candidate)
    {
        var q = TextNormalizer.Fold(query.Trim());
        if (q.Length == 0) return 0;
        var c = TextNormalizer.Fold(candidate);

        int idx = c.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0)
        {
            bool wordStart = idx == 0 || !char.IsLetterOrDigit(c[idx - 1]);
            return 10_000 - idx * 10 - (c.Length - q.Length) + (wordStart ? 2_000 : 0);
        }

        // all query words present somewhere
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => c.Contains(w, StringComparison.Ordinal)))
            return 5_000 - c.Length;

        // subsequence match, rewarding consecutive characters and word starts
        int score = 0, ci = 0, run = 0;
        foreach (var ch in q)
        {
            if (ch == ' ') continue;
            int found = c.IndexOf(ch, ci);
            if (found < 0) return -1;
            run = found == ci ? run + 1 : 0;
            bool ws = found == 0 || !char.IsLetterOrDigit(c[found - 1]);
            score += 10 + run * 5 + (ws ? 15 : 0) - Math.Min(found - ci, 10);
            ci = found + 1;
        }
        return Math.Max(1, score);
    }

    public static T? Best<T>(string query, IEnumerable<T> items, Func<T, string> text) where T : class
    {
        T? best = null;
        int bestScore = -1;
        foreach (var it in items)
        {
            int s = Score(query, text(it));
            if (s > bestScore) { bestScore = s; best = it; }
        }
        return bestScore > 0 ? best : null;
    }
}
