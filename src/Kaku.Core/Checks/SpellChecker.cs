using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Kaku.Core.Text;
using WeCantSpell.Hunspell;

namespace Kaku.Core.Checks;

/// <summary>A word in a block's visible text that the dictionary does not know.</summary>
public readonly record struct Misspelling(int Start, int Length, string Word);

/// <summary>
/// Spanish spell checking (Hunspell es_ES) plus the user's own words. Proper names are skipped:
/// a capitalized word that does not start a sentence is assumed to be a name (Suzuki, Kōtō…).
/// Thread-safe for checking; <see cref="Suggest"/> is slower and meant for one word at a time.
/// </summary>
public sealed class SpellChecker
{
    private readonly WordList _words;
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _user = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public SpellChecker(Stream dic, Stream aff)
    {
        _words = WordList.CreateFromStreams(dic, aff);
        foreach (var w in AcceptedWords) _ignored.Add(w);
    }

    /// <summary>
    /// Learns the book's names: capitalized words found in mid-sentence (so "Suzuki" at the start of a
    /// sentence is a name too) that the dictionary does not know in lowercase.
    /// </summary>
    public void LearnNames(IEnumerable<string> texts)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in texts)
            foreach (var (start, len) in WordCounter.Words(t))
                if (char.IsUpper(t[start]) && len > 1 && !SentenceStart(t, start)) found.Add(t.Substring(start, len));
        lock (_lock)
        {
            BookNames.Clear();
            foreach (var w in found)
                if (!_words.Check(w.ToLower(CultureInfo.GetCultureInfo("es-ES")))) BookNames.Add(w);
        }
        Changed();
    }

    public bool IgnoreProperNames { get; set; } = true;

    /// <summary>Japanese words a Spanish translation keeps on purpose, and common loanwords missing from the dictionary.</summary>
    public static readonly string[] AcceptedWords =
    [
        "kun", "chan", "san", "sama", "sensei", "senpai", "sempai", "kohai", "kōhai", "onii", "onee", "nee", "nii", "baka", "kawaii",
        "sake", "udon", "ramen", "soba", "sushi", "sashimi", "natto", "nattō", "tofu", "miso", "wasabi", "tempura", "onigiri", "bento", "obento",
        "gyoza", "takoyaki", "okonomiyaki", "mochi", "matcha", "oden", "yakitori", "izakaya", "konbini", "ryokan", "onsen", "tatami", "futon",
        "kimono", "yukata", "geta", "manga", "anime", "otaku", "yen", "karaoke", "ninja", "samurai", "katana", "yakuza", "shinkansen", "sakura",
        "torii", "kami", "gaijin", "tanuki", "kanji", "hiragana", "katakana", "furigana", "haiku", "origami", "bonsái", "ikebana", "dojo", "shogi", "pachinko",
        "smartphone", "snowboard", "email", "e-mail", "online", "software", "hardware", "chat", "selfie", "wifi", "router", "influencer", "playlist",
        "youtuber", "streaming", "podcast", "hashtag", "tablet", "app", "apps", "manager", "cosplay", "idol", "show", "reality", "sándwich",
        "jaja", "jajaja", "jeje", "jejeje", "mmm", "mm", "hmm", "hum", "uf", "ups", "bah", "puaj", "eh", "ah", "oh", "uh",
    ];

    /// <summary>Capitalized words the book uses as names (seen capitalized in mid-sentence).</summary>
    public HashSet<string> BookNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Changes whenever the set of accepted words changes (views cache by it).</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<string> UserWords
    {
        get
        {
            lock (_lock) return _user.ToList();
        }
    }

    public void AddUserWords(IEnumerable<string> words)
    {
        lock (_lock)
            foreach (var w in words.Select(w => w.Trim()).Where(w => w.Length > 0)) _user.Add(w);
        Changed();
    }

    public void AddUserWord(string word) => AddUserWords([word]);

    public void RemoveUserWord(string word)
    {
        lock (_lock) _user.Remove(word);
        Changed();
    }

    /// <summary>Accepted for this session only.</summary>
    public void Ignore(string word)
    {
        lock (_lock) _ignored.Add(word);
        Changed();
    }

    private void Changed()
    {
        _cache.Clear();
        Version++;
    }

    public bool IsCorrect(string word)
    {
        if (_cache.TryGetValue(word, out var ok)) return ok;
        ok = CheckUncached(word);
        _cache[word] = ok;
        return ok;
    }

    private bool CheckUncached(string word)
    {
        lock (_lock)
            if (_user.Contains(word) || _ignored.Contains(word)) return true;
        if (_words.Check(word)) return true;
        // sentence-initial capital: the lowercase form is enough
        var lower = word.ToLower(CultureInfo.GetCultureInfo("es-ES"));
        if (lower != word && _words.Check(lower)) return true;
        // apostrophes from the e-reader ("d’Artagnan") and typographic variants
        var plain = word.Replace('’', '\'');
        return plain != word && _words.Check(plain);
    }

    public List<string> Suggest(string word, int max = 8)
    {
        var s = _words.Suggest(word).Take(max).ToList();
        if (s.Count == 0 && char.IsUpper(word[0]))
            s = _words.Suggest(word.ToLower(CultureInfo.GetCultureInfo("es-ES"))).Take(max)
                .Select(x => char.ToUpper(x[0]) + x[1..]).ToList();
        return s;
    }

    /// <summary>Misspelled words of a block (its WordText: placeholders and note numbers blanked).</summary>
    public List<Misspelling> Check(string wordText)
    {
        var result = new List<Misspelling>();
        foreach (var (start, len) in WordCounter.Words(wordText))
        {
            var w = wordText.Substring(start, len);
            if (Skip(wordText, start, w)) continue;
            if (!IsCorrect(w)) result.Add(new Misspelling(start, len, w));
        }
        return result;
    }

    private bool Skip(string text, int start, string w)
    {
        if (w.Length < 2) return true;
        if (WordCounter.IsCjk(w[0])) return true;                       // Japanese is not Spanish
        if (w.Any(char.IsDigit)) return true;                            // 3D, 2.º, 1990s
        if (w.All(c => char.IsUpper(c) || !char.IsLetter(c))) return true; // siglas: ONU, DNI
        if (w.Any(c => c is 'ā' or 'ī' or 'ū' or 'ē' or 'ō' or 'Ā' or 'Ī' or 'Ū' or 'Ē' or 'Ō')) return true; // romanized Japanese (Kōtō)
        if (char.IsUpper(w[0]) && BookNames.Contains(w)) return true;
        if (IgnoreProperNames && char.IsUpper(w[0]) && !SentenceStart(text, start)) return true;
        return false;
    }

    /// <summary>True if only punctuation, quotes, dashes or spaces lie between the word and the previous sentence end.</summary>
    public static bool SentenceStart(string text, int start)
    {
        for (int i = start - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c)) return false;
            if (c is '.' or '!' or '?' or '…' or ':' or '¿' or '¡' or '—' or '–' or '«' or '“' or '"' or '\'' or '‘' or '(' or '[' or '-' or '「' or '『') return true;
        }
        return true; // start of the paragraph
    }

    /// <summary>Loads the user dictionary (one word per line).</summary>
    public static IEnumerable<string> ReadUserFile(string path) =>
        File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')) : [];

    public void WriteUserFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var words = UserWords.Order(StringComparer.Create(CultureInfo.GetCultureInfo("es-ES"), true)).ToList();
        File.WriteAllLines(path, words.Prepend("# Diccionario personal de Kaku: una palabra por línea"), new UTF8Encoding(false));
    }
}
