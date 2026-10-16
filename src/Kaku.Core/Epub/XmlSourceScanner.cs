namespace Kaku.Core.Epub;

/// <summary>Where an element sits in the source text (offsets into the string).</summary>
public readonly record struct ElementRange(int OuterStart, int ContentStart, int ContentEnd, int OuterEnd, bool SelfClosing);

/// <summary>
/// Finds the source range of every element of a well-formed XML document, in document order
/// (the same order as <c>XDocument.Root.DescendantsAndSelf()</c>). Used to splice edits into the
/// original text so that untouched markup stays byte-identical.
/// </summary>
public static class XmlSourceScanner
{
    public static List<ElementRange> Scan(string s)
    {
        var result = new List<ElementRange>();
        var open = new Stack<int>(); // indexes into result
        int i = 0, n = s.Length;
        while (i < n)
        {
            int lt = s.IndexOf('<', i);
            if (lt < 0) break;
            if (Starts(s, lt, "<!--")) { i = End(s, lt + 4, "-->"); continue; }
            if (Starts(s, lt, "<![CDATA[")) { i = End(s, lt + 9, "]]>"); continue; }
            if (Starts(s, lt, "<?")) { i = End(s, lt + 2, "?>"); continue; }
            if (Starts(s, lt, "<!")) { i = SkipDeclaration(s, lt); continue; }
            if (lt + 1 < n && s[lt + 1] == '/')
            {
                int gt = s.IndexOf('>', lt);
                if (gt < 0) break;
                if (open.Count > 0)
                {
                    int idx = open.Pop();
                    var r = result[idx];
                    result[idx] = r with { ContentEnd = lt, OuterEnd = gt + 1 };
                }
                i = gt + 1;
                continue;
            }
            int tagEnd = TagEnd(s, lt);
            if (tagEnd < 0) break;
            bool selfClosing = s[tagEnd - 2] == '/';
            result.Add(new ElementRange(lt, tagEnd, tagEnd, tagEnd, selfClosing));
            if (!selfClosing) open.Push(result.Count - 1);
            i = tagEnd;
        }
        return result;
    }

    private static bool Starts(string s, int at, string token) => string.CompareOrdinal(s, at, token, 0, token.Length) == 0;

    private static int End(string s, int from, string token)
    {
        int e = s.IndexOf(token, from, StringComparison.Ordinal);
        return e < 0 ? s.Length : e + token.Length;
    }

    /// <summary>Offset just after the '&gt;' that closes the start tag at <paramref name="lt"/>, honouring quoted attribute values.</summary>
    private static int TagEnd(string s, int lt)
    {
        char quote = '\0';
        for (int i = lt + 1; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return -1;
    }

    /// <summary>Skips &lt;!DOCTYPE …&gt; including an internal subset in brackets.</summary>
    private static int SkipDeclaration(string s, int lt)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = lt + 2; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '[': depth++; break;
                case ']': depth--; break;
                case '>' when depth <= 0: return i + 1;
            }
        }
        return s.Length;
    }
}
