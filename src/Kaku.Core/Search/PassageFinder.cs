using Kaku.Core.Text;

namespace Kaku.Core.Search;

/// <summary>A place in the book that resembles the pasted text.</summary>
public sealed record PassageMatch(
    TextLocation Start,
    TextLocation End,
    long WordIndex,
    double Score,
    int MatchedWords,
    int QueryWords,
    string Before,
    string Match,
    string After);

/// <summary>
/// "Paste a few words from the e-reader and jump there": a word-level fuzzy search over the whole book,
/// tolerant to accents, case, punctuation and quote styles, line breaks, paragraph boundaries, inline
/// markup and a word or two that differ. Build once per book state; rebuild after edits.
/// </summary>
public sealed class PassageFinder
{
    private const int MaxQueryWords = 60;
    private const int MaxCandidates = 4000;

    private readonly Book _book;
    private readonly List<Token> _tokens = new();
    private readonly Dictionary<string, List<int>> _index = new(StringComparer.Ordinal);

    private readonly record struct Token(int Chapter, int Block, int Start, int Length, string Word, long WordIndex);

    public PassageFinder(Book book)
    {
        _book = book;
        foreach (var c in book.Chapters)
        {
            foreach (var b in c.Blocks)
            {
                if (b.IsNote) continue; // same words as the positions (% / word number)
                int k = 0;
                foreach (var (s, len) in WordCounter.Words(b.WordText))
                {
                    var w = Normalize(b.WordText.Substring(s, len));
                    var t = new Token(c.Index, b.Index, s, len, w, c.WordStart + b.WordStart + k++);
                    if (!_index.TryGetValue(w, out var list)) _index[w] = list = new List<int>();
                    list.Add(_tokens.Count);
                    _tokens.Add(t);
                }
            }
        }
        Revisions = book.Chapters.Select(c => c.Revision).ToArray();
    }

    /// <summary>Chapter revisions at build time; compare with the book to know if a rebuild is needed.</summary>
    public int[] Revisions { get; }

    public bool IsCurrent(Book book) =>
        ReferenceEquals(book, _book) && book.Chapters.Count == Revisions.Length && book.Chapters.All(c => c.Revision == Revisions[c.Index]);

    private static string Normalize(string w) => TextNormalizer.Fold(w).Replace('’', '\'');

    public List<PassageMatch> Find(string query, int max = 10)
    {
        var q = WordCounter.Words(query).Select(x => Normalize(query.Substring(x.Start, x.Length))).Take(MaxQueryWords).ToArray();
        if (q.Length == 0 || _tokens.Count == 0) return [];

        // anchor on the rarest query words that exist in the book (exactly or with a typo)
        var anchors = q.Select((w, i) => (Word: w, QIndex: i, Hits: _index.GetValueOrDefault(w)))
            .Where(a => a.Hits != null)
            .OrderBy(a => a.Hits!.Count)
            .Take(3)
            .ToList();
        if (anchors.Count == 0)
        {
            // nothing exact: try typo-tolerant anchors among the longer words
            foreach (var (w, i) in q.Select((w, i) => (w, i)).Where(x => x.w.Length >= 5).OrderByDescending(x => x.w.Length).Take(2))
            {
                var similar = _index.Where(kv => Math.Abs(kv.Key.Length - w.Length) <= 1 && Similar(kv.Key, w)).SelectMany(kv => kv.Value).ToList();
                if (similar.Count > 0) anchors.Add((w, i, similar));
            }
        }

        int window = q.Length + 4 + q.Length / 5;
        var tried = new HashSet<int>();
        var found = new List<(int First, int Last, int Matched)>();
        int candidates = 0;
        foreach (var a in anchors)
        {
            foreach (var p in a.Hits!)
            {
                int start = Math.Max(0, p - a.QIndex - 2);
                if (!tried.Add(start)) continue;
                if (++candidates > MaxCandidates) break;
                if (Align(q, start, Math.Min(_tokens.Count, start + window)) is { } r) found.Add(r);
            }
        }

        var results = new List<PassageMatch>();
        foreach (var (first, last, matched) in found.OrderByDescending(f => f.Matched).ThenBy(f => f.First))
        {
            if (results.Count >= max) break;
            if (results.Any(r => Overlaps(r, first, last))) continue;
            double score = (double)matched / q.Length;
            if (score < 0.4 && results.Count > 0) break;
            results.Add(MakeMatch(first, last, matched, q.Length, score));
        }
        return results;
    }

    private bool Overlaps(PassageMatch r, int first, int last) =>
        _tokens[first].WordIndex <= r.WordIndex + r.QueryWords + 2 && _tokens[last].WordIndex >= r.WordIndex - 2;

    /// <summary>Longest common subsequence of the query and a window of the book (fuzzy word equality).</summary>
    private (int First, int Last, int Matched)? Align(string[] q, int start, int end)
    {
        int n = q.Length, m = end - start;
        if (m <= 0) return null;
        var dp = new int[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
            dp[i, j] = Similar(q[i - 1], _tokens[start + j - 1].Word)
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        int matched = dp[n, m];
        if (matched == 0) return null;

        // walk back to find the first and last matched book tokens
        int a = n, b = m, first = -1, last = -1;
        while (a > 0 && b > 0)
        {
            if (Similar(q[a - 1], _tokens[start + b - 1].Word) && dp[a, b] == dp[a - 1, b - 1] + 1)
            {
                if (last < 0) last = start + b - 1;
                first = start + b - 1;
                a--;
                b--;
            }
            else if (dp[a - 1, b] >= dp[a, b - 1]) a--;
            else b--;
        }
        return (first, last, matched);
    }

    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int min = Math.Min(a.Length, b.Length);
        if (min < 4 || Math.Abs(a.Length - b.Length) > 2) return false;
        return Levenshtein(a, b, min >= 8 ? 2 : 1) <= (min >= 8 ? 2 : 1);
    }

    private static int Levenshtein(string a, string b, int limit)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > limit) return limit + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private PassageMatch MakeMatch(int first, int last, int matched, int queryWords, double score)
    {
        var a = _tokens[first];
        var z = _tokens[last];
        var blockA = _book.Chapters[a.Chapter].Blocks[a.Block].Text;
        string before = blockA[Math.Max(0, a.Start - 40)..a.Start];
        if (a.Start > 40) before = "…" + before.TrimStart();
        string match, after;
        if (a.Chapter == z.Chapter && a.Block == z.Block)
        {
            match = blockA[a.Start..(z.Start + z.Length)];
            int e = z.Start + z.Length;
            after = blockA[e..Math.Min(blockA.Length, e + 50)] + (e + 50 < blockA.Length ? "…" : "");
        }
        else
        {
            var blockZ = _book.Chapters[z.Chapter].Blocks[z.Block].Text;
            match = blockA[a.Start..] + " ¶ " + blockZ[..(z.Start + z.Length)];
            int e = z.Start + z.Length;
            after = blockZ[e..Math.Min(blockZ.Length, e + 50)] + (e + 50 < blockZ.Length ? "…" : "");
        }
        if (match.Length > 260) match = match[..120] + " … " + match[^120..];
        return new PassageMatch(
            new TextLocation(a.Chapter, a.Block, a.Start),
            new TextLocation(z.Chapter, z.Block, z.Start + z.Length),
            a.WordIndex, score, matched, queryWords, before, match, after);
    }
}
