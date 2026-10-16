using System.Text.RegularExpressions;
using Kaku.Core.Editing;
using Kaku.Core.Text;

namespace Kaku.Core.Checks;

public enum CheckKind
{
    Japanese,
    ModelTokens,
    Spelling,
    RepeatedWord,
    MixedQuotes,
    StraightQuotes,
    JapaneseQuotes,
    HyphenDialogue,
    ThreeDots,
    SpaceBeforePunct,
    MissingSpaceAfterPunct,
    OddSpaces,
    UnpairedMarks,
    EmptyParagraphs,
    Epub,
}

public enum Severity { Error, Warning, Info }

/// <summary>
/// A problem found by the checks. Text problems point to a block of a chapter (offsets in its visible text);
/// EPUB problems point to a file and line. <see cref="Fix"/> is an automatic correction when there is an obvious one.
/// </summary>
public sealed record Problem(
    CheckKind Kind,
    Severity Severity,
    string Message,
    int Chapter,
    int Block,
    int Start,
    int Length,
    int Revision,
    TextChange? Fix = null,
    string? Path = null,
    int Line = 0,
    string Context = "",
    string? Word = null);

/// <summary>Immutable copy of what the checks need, taken on the UI thread so checks can run in the background.</summary>
public sealed record BlockSnapshot(int Chapter, int Block, string Text, string WordText, bool IsNote, bool IsEmpty, int Revision);

public static partial class TextChecks
{
    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー々〆]+")]
    private static partial Regex JapaneseRe();

    [GeneratedRegex(@"<\|[^|>]{0,40}\|>|<\|channel>|</?think>|\[/?INST\]")]
    private static partial Regex TokensRe();

    [GeneratedRegex(@"\b(\p{L}{2,})(\s+)\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedRe();

    [GeneratedRegex(@"\.\.\.")]
    private static partial Regex ThreeDotsRe();

    [GeneratedRegex(@"(?<=\p{L}|\p{N}|[»”)…])[  ]+(?=[,.;:!?»”)](?!\.\.)|…(?!\p{L}))")]
    private static partial Regex SpaceBeforeRe();

    [GeneratedRegex(@"(?<=\p{Ll}{2})[,;](?=\p{L})|(?<=\p{Ll}{2})[.?!](?=\p{Lu}\p{Ll})")]
    private static partial Regex MissingSpaceRe();

    [GeneratedRegex(@"　+| {2,}|  +")]
    private static partial Regex OddSpacesRe();

    [GeneratedRegex(@"^[-–]\s?(?=\S)")]
    private static partial Regex HyphenDialogueRe();

    public static List<BlockSnapshot> Snapshot(Book book, IReadOnlyCollection<int>? chapters = null) =>
        book.Chapters.Where(c => c.ParseError == null && (chapters == null || chapters.Contains(c.Index)))
            .SelectMany(c => c.Blocks.Select(b => new BlockSnapshot(c.Index, b.Index, b.Text, b.WordText, b.IsNote,
                b.Text.Trim().Trim(TextExtractor.LineBreakChar).Trim().Length == 0 && b.Kind != BlockKind.Image, c.Revision)))
            .ToList();

    /// <summary>Which quote style the book mostly uses (to flag the others).</summary>
    public static char DominantQuote(IEnumerable<BlockSnapshot> blocks)
    {
        int g = 0, c = 0, s = 0;
        foreach (var b in blocks)
            foreach (var ch in b.Text)
                switch (ch)
                {
                    case '«': g++; break;
                    case '“': c++; break;
                    case '"': s++; break;
                }
        return g >= c && g >= s ? '«' : c >= s ? '“' : '"';
    }

    /// <summary>Runs every text check (spelling only if a checker is given) over the snapshot.</summary>
    public static List<Problem> Run(IReadOnlyList<BlockSnapshot> blocks, SpellChecker? spell, char? dominantQuote = null, CancellationToken ct = default)
    {
        var problems = new List<Problem>();
        var dominant = dominantQuote ?? DominantQuote(blocks);
        if (spell != null && blocks.Count > 0) spell.LearnNames(blocks.Select(b => b.WordText));
        int emptyRun = 0;
        BlockSnapshot? emptyStart = null;
        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (b.IsEmpty)
            {
                if (emptyRun++ == 0) emptyStart = b;
                if (emptyRun == 3)
                    problems.Add(Make(emptyStart!, CheckKind.EmptyParagraphs, Severity.Info, "Tres o más párrafos vacíos seguidos", 0, 0));
                continue;
            }
            emptyRun = 0;
            CheckBlock(b, dominant, spell, problems);
        }
        return problems;
    }

    private static Problem Make(BlockSnapshot b, CheckKind kind, Severity sev, string msg, int start, int length, TextChange? fix = null, string? word = null) =>
        new(kind, sev, msg, b.Chapter, b.Block, start, length, b.Revision, fix, Context: Context(b.Text, start, length), Word: word);

    private static string Context(string text, int start, int length)
    {
        int a = Math.Max(0, start - 35), z = Math.Min(text.Length, start + length + 45);
        return (a > 0 ? "…" : "") + text[a..z].Replace('\n', ' ') + (z < text.Length ? "…" : "");
    }

    private static void CheckBlock(BlockSnapshot b, char dominant, SpellChecker? spell, List<Problem> problems)
    {
        var t = b.WordText; // placeholders blanked: same offsets as Text

        foreach (Match m in JapaneseRe().Matches(t))
            if (m.Value.Any(c => c is not ('・' or 'ー' or '･')))
                problems.Add(Make(b, CheckKind.Japanese, Severity.Error, $"Texto japonés sin traducir: «{m.Value}»", m.Index, m.Length));

        foreach (Match m in TokensRe().Matches(b.Text))
            problems.Add(Make(b, CheckKind.ModelTokens, Severity.Error, $"Resto del modelo de traducción: «{m.Value}»", m.Index, m.Length, new TextChange(m.Index, m.Index + m.Length, "")));

        foreach (Match m in RepeatedRe().Matches(t))
        {
            // "que que" is almost always an error; skip a few legitimate doublings
            var w = m.Groups[1].Value.ToLowerInvariant();
            if (w is "no" or "sí" or "si" or "ja" or "je" or "ay" or "eh" or "oh" or "bueno" or "muy" or "más") continue;
            int second = m.Groups[2].Index;
            problems.Add(Make(b, CheckKind.RepeatedWord, Severity.Warning, $"Palabra repetida: «{m.Groups[1].Value}»", m.Index, m.Length,
                new TextChange(second, m.Index + m.Length, "")));
        }

        foreach (Match m in ThreeDotsRe().Matches(b.Text))
            problems.Add(Make(b, CheckKind.ThreeDots, Severity.Info, "Tres puntos en lugar de puntos suspensivos (…)", m.Index, 3, new TextChange(m.Index, m.Index + 3, "…")));

        foreach (Match m in SpaceBeforeRe().Matches(t))
            problems.Add(Make(b, CheckKind.SpaceBeforePunct, Severity.Warning, "Espacio antes de un signo de puntuación", m.Index, m.Length, new TextChange(m.Index, m.Index + m.Length, "")));

        foreach (Match m in MissingSpaceRe().Matches(t))
            problems.Add(Make(b, CheckKind.MissingSpaceAfterPunct, Severity.Warning, $"Falta un espacio después de «{m.Value}»", m.Index, 1, new TextChange(m.Index + 1, m.Index + 1, " ")));

        foreach (Match m in OddSpacesRe().Matches(t))
        {
            if (m.Index == 0 && m.Value.All(c => c == '　')) // indentation left from the Japanese layout
            {
                problems.Add(Make(b, CheckKind.OddSpaces, Severity.Info, "Sangría japonesa (espacio ideográfico) al principio", 0, m.Length, new TextChange(0, m.Length, "")));
                continue;
            }
            problems.Add(Make(b, CheckKind.OddSpaces, Severity.Info, m.Value.Contains('　') ? "Espacio japonés (ideográfico) en el texto" : "Espacios dobles visibles",
                m.Index, m.Length, new TextChange(m.Index, m.Index + m.Length, " ")));
        }

        var hd = HyphenDialogueRe().Match(b.Text);
        if (hd.Success)
            problems.Add(Make(b, CheckKind.HyphenDialogue, Severity.Warning, "Diálogo con guion en lugar de raya (—)", 0, hd.Length, new TextChange(0, hd.Length, "—")));

        // quotes
        for (int i = 0; i < b.Text.Length; i++)
        {
            char c = b.Text[i];
            if (c is '「' or '」' or '『' or '』')
            {
                var open = c is '「' or '『';
                var rep = dominant switch { '«' => open ? "«" : "»", '“' => open ? "“" : "”", _ => "\"" };
                problems.Add(Make(b, CheckKind.JapaneseQuotes, Severity.Warning, $"Comillas japonesas «{c}»", i, 1, new TextChange(i, i + 1, rep)));
            }
            else if (c is '“' or '”' && dominant == '«')
                problems.Add(Make(b, CheckKind.MixedQuotes, Severity.Info, "Comillas inglesas en un libro con comillas angulares", i, 1, new TextChange(i, i + 1, c == '“' ? "«" : "»")));
            else if (c is '«' or '»' && dominant == '“')
                problems.Add(Make(b, CheckKind.MixedQuotes, Severity.Info, "Comillas angulares en un libro con comillas inglesas", i, 1, new TextChange(i, i + 1, c == '«' ? "“" : "”")));
        }
        // straight quotes: fixable when they pair up in the paragraph
        var straight = Enumerable.Range(0, b.Text.Length).Where(i => b.Text[i] == '"').ToList();
        if (straight.Count > 0 && dominant != '"')
        {
            bool pairs = straight.Count % 2 == 0;
            for (int k = 0; k < straight.Count; k++)
            {
                int i = straight[k];
                var rep = dominant == '«' ? (k % 2 == 0 ? "«" : "»") : (k % 2 == 0 ? "“" : "”");
                problems.Add(Make(b, CheckKind.StraightQuotes, Severity.Info, "Comillas rectas (\")", i, 1, pairs ? new TextChange(i, i + 1, rep) : null));
            }
        }

        // ¿…? and ¡…!
        int q1 = t.Count(c => c == '¿'), q2 = t.Count(c => c == '?');
        int e1 = t.Count(c => c == '¡'), e2 = t.Count(c => c == '!');
        if (q1 != q2 && q2 > 0 || q1 > q2)
        {
            int at = q1 < q2 ? t.IndexOf('?') : t.IndexOf('¿');
            problems.Add(Make(b, CheckKind.UnpairedMarks, Severity.Info, q1 < q2 ? "Falta el signo de apertura (¿)" : "Falta el signo de cierre (?)", Math.Max(0, at), 1));
        }
        if (e1 != e2 && e2 > 0 || e1 > e2)
        {
            int at = e1 < e2 ? t.IndexOf('!') : t.IndexOf('¡');
            problems.Add(Make(b, CheckKind.UnpairedMarks, Severity.Info, e1 < e2 ? "Falta el signo de apertura (¡)" : "Falta el signo de cierre (!)", Math.Max(0, at), 1));
        }

        if (spell != null)
            foreach (var m in spell.Check(t))
                problems.Add(Make(b, CheckKind.Spelling, Severity.Warning, $"Ortografía: «{m.Word}»", m.Start, m.Length, word: m.Word));
    }
}
