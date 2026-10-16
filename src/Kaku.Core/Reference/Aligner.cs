using Kaku.Core.Text;

namespace Kaku.Core.Reference;

/// <summary>
/// Paragraph alignment between a translated chapter and its original: a global sequence alignment
/// (like a diff) whose match cost compares the "shape" of two paragraphs (length, empty/image/heading,
/// numbers, dialogue). Inserted, deleted, split and merged paragraphs become gaps; every paragraph still
/// maps to its nearest counterpart so the reference view always shows something sensible.
/// </summary>
public static class Aligner
{
    private const double GapCost = 1.4;

    private enum Shape { Empty, Image, Heading, Text }

    private readonly record struct Features(Shape Shape, int Letters, string Digits, bool Dialogue, bool Skip);

    /// <summary>Result: for each translated block the original block (or -1), and the reverse.</summary>
    public sealed record Alignment(int[] ToOriginal, int[] ToTranslation, bool[] Exact)
    {
        /// <summary>Share of translated paragraphs (with text) that got a direct match.</summary>
        public double Coverage { get; init; }
    }

    /// <summary>A pinned pair (translated block ↔ original block) that the alignment must respect.</summary>
    public readonly record struct Pin(int Translated, int Original);

    public static Alignment Align(IReadOnlyList<ContentBlock> translated, IReadOnlyList<ContentBlock> original, IReadOnlyList<Pin>? pins = null)
    {
        var a = translated.Select(Describe).ToArray();
        var b = original.Select(Describe).ToArray();
        // expected length ratio for this chapter (Spanish is ~2–3 letters per Japanese character)
        double la = a.Where(f => f.Shape == Shape.Text && !f.Skip).Sum(f => (double)f.Letters);
        double lb = b.Where(f => f.Shape == Shape.Text && !f.Skip).Sum(f => (double)f.Letters);
        double ratio = lb > 0 && la > 0 ? la / lb : 1;

        var toO = Enumerable.Repeat(-1, a.Length).ToArray();
        var toT = Enumerable.Repeat(-1, b.Length).ToArray();
        var exact = new bool[a.Length];

        // pins split the problem into independent segments
        var anchors = (pins ?? []).Where(p => p.Translated >= 0 && p.Translated < a.Length && p.Original >= 0 && p.Original < b.Length)
            .OrderBy(p => p.Translated).ToList();
        var cleaned = new List<Pin>();
        foreach (var p in anchors)
            if (cleaned.Count == 0 || p.Translated > cleaned[^1].Translated && p.Original > cleaned[^1].Original) cleaned.Add(p);

        int ta = 0, tb = 0;
        foreach (var p in cleaned.Append(new Pin(a.Length, b.Length)))
        {
            AlignSegment(a, b, ta, p.Translated, tb, p.Original, ratio, toO, toT, exact);
            if (p.Translated < a.Length)
            {
                toO[p.Translated] = p.Original;
                toT[p.Original] = p.Translated;
                exact[p.Translated] = true;
            }
            ta = p.Translated + 1;
            tb = p.Original + 1;
        }

        int textual = 0, matched = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Skip || a[i].Shape != Shape.Text) continue;
            textual++;
            if (exact[i]) matched++;
        }
        FillGaps(toO, a.Select(f => f.Skip).ToArray());
        FillGaps(toT, b.Select(f => f.Skip).ToArray());
        return new Alignment(toO, toT, exact) { Coverage = textual == 0 ? 1 : (double)matched / textual };
    }

    private static Features Describe(ContentBlock block)
    {
        var t = block.WordText;
        int letters = t.Count(c => char.IsLetterOrDigit(c));
        var shape = block.Kind == BlockKind.Image ? Shape.Image
            : block.Kind == BlockKind.Heading ? Shape.Heading
            : letters == 0 ? Shape.Empty
            : Shape.Text;
        var digits = new string(t.Where(c => c is >= '0' and <= '9' || c is >= '０' and <= '９').Select(c => c >= '０' ? (char)(c - '０' + '0') : c).ToArray());
        var first = block.Text.TrimStart(' ', '　').FirstOrDefault();
        bool dialogue = first is '「' or '『' or '—' or '–' or '“' or '«' or '"' or '-';
        return new Features(shape, letters, digits, dialogue, block.IsNote);
    }

    private static double MatchCost(Features x, Features y, double ratio)
    {
        if (x.Skip || y.Skip) return 99;
        double cost = 0;
        if (x.Shape != y.Shape)
        {
            // a heading may come out as a plain paragraph and the other way round
            bool textLike = x.Shape is Shape.Text or Shape.Heading && y.Shape is Shape.Text or Shape.Heading;
            cost += textLike ? 0.6 : 2.5;
        }
        if (x.Shape is Shape.Text or Shape.Heading && y.Shape is Shape.Text or Shape.Heading)
        {
            double r = Math.Log((x.Letters + 6.0) / (y.Letters * ratio + 6.0));
            cost += Math.Min(3, Math.Abs(r) * 1.3);
            if (x.Digits.Length > 0 || y.Digits.Length > 0) cost += x.Digits == y.Digits ? -0.4 : 0.5;
            if (x.Dialogue != y.Dialogue) cost += 0.35;
        }
        return cost;
    }

    private static void AlignSegment(Features[] a, Features[] b, int a0, int a1, int b0, int b1, double ratio, int[] toO, int[] toT, bool[] exact)
    {
        int n = a1 - a0, m = b1 - b0;
        if (n <= 0 || m <= 0) return;
        var dp = new double[n + 1, m + 1];
        var move = new byte[n + 1, m + 1]; // 1 = match, 2 = gap in original (skip translated), 3 = gap in translation
        for (int i = 1; i <= n; i++) { dp[i, 0] = i * GapCost; move[i, 0] = 2; }
        for (int j = 1; j <= m; j++) { dp[0, j] = j * GapCost; move[0, j] = 3; }
        for (int i = 1; i <= n; i++)
        {
            var fa = a[a0 + i - 1];
            for (int j = 1; j <= m; j++)
            {
                var fb = b[b0 + j - 1];
                // positional prior: paragraphs at the same relative place are more likely to match
                double drift = Math.Abs((i - 1) * (double)m / n - (j - 1));
                double diag = dp[i - 1, j - 1] + MatchCost(fa, fb, ratio) + Math.Min(0.6, drift * 0.03);
                double up = dp[i - 1, j] + (fa.Skip ? 0 : GapCost);
                double left = dp[i, j - 1] + (fb.Skip ? 0 : GapCost);
                if (diag <= up && diag <= left) { dp[i, j] = diag; move[i, j] = 1; }
                else if (up <= left) { dp[i, j] = up; move[i, j] = 2; }
                else { dp[i, j] = left; move[i, j] = 3; }
            }
        }
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            switch (move[x, y])
            {
                case 1:
                    if (!a[a0 + x - 1].Skip && !b[b0 + y - 1].Skip)
                    {
                        toO[a0 + x - 1] = b0 + y - 1;
                        toT[b0 + y - 1] = a0 + x - 1;
                        exact[a0 + x - 1] = true;
                    }
                    x--;
                    y--;
                    break;
                case 2:
                    x--;
                    break;
                default:
                    y--;
                    break;
            }
        }
    }

    /// <summary>Unmatched entries take the previous match (or the next one at the start), skipped ones stay -1.</summary>
    private static void FillGaps(int[] map, bool[] skip)
    {
        int last = -1;
        for (int i = 0; i < map.Length; i++)
        {
            if (skip[i]) { map[i] = -1; continue; }
            if (map[i] >= 0) last = map[i];
            else if (last >= 0) map[i] = last;
        }
        int next = -1;
        for (int i = map.Length - 1; i >= 0; i--)
        {
            if (skip[i]) continue;
            if (map[i] >= 0) next = map[i];
            else if (next >= 0) map[i] = next;
        }
    }
}
