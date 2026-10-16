namespace Kaku.Core.Epub;

/// <summary>Helpers for zip-internal paths ("OEBPS/Text/ch1.xhtml") and relative hrefs.</summary>
public static class EpubPath
{
    /// <summary>Directory part including the trailing slash, or "" for root files.</summary>
    public static string DirectoryOf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..(i + 1)];
    }

    public static string FileName(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string Extension(string path)
    {
        var name = FileName(path);
        int i = name.LastIndexOf('.');
        return i < 0 ? "" : name[i..].ToLowerInvariant();
    }

    /// <summary>Splits "ch1.xhtml#p3" into path and fragment (fragment without '#', null if absent). Query strings are dropped.</summary>
    public static (string Path, string? Fragment) SplitHref(string href)
    {
        string? fragment = null;
        int hash = href.IndexOf('#');
        if (hash >= 0)
        {
            fragment = href[(hash + 1)..];
            href = href[..hash];
        }
        int q = href.IndexOf('?');
        if (q >= 0) href = href[..q];
        return (href, string.IsNullOrEmpty(fragment) ? null : Uri.UnescapeDataString(fragment));
    }

    /// <summary>True for hrefs that point outside the book (http:, mailto:, ...).</summary>
    public static bool IsExternal(string href) =>
        href.Contains("://", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves an href relative to the file that contains it. Returns the zip path (fragment removed).</summary>
    public static string Resolve(string baseFile, string href)
    {
        var (p, _) = SplitHref(href.Trim());
        p = Uri.UnescapeDataString(p.Replace('\\', '/'));
        if (p.Length == 0) return baseFile;
        var combined = p.StartsWith('/') ? p.TrimStart('/') : DirectoryOf(baseFile) + p;
        return Normalize(combined);
    }

    /// <summary>Relative href from <paramref name="fromFile"/> to <paramref name="toPath"/> (both zip paths), URL-escaped.</summary>
    public static string Relative(string fromFile, string toPath, string? fragment = null)
    {
        var from = DirectoryOf(fromFile).TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = toPath.Split('/');
        int common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common] == to[common]) common++;
        var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common).Select(Uri.EscapeDataString));
        var rel = string.Join('/', parts);
        if (toPath == fromFile) rel = Uri.EscapeDataString(FileName(toPath));
        return fragment == null ? rel : rel + "#" + Uri.EscapeDataString(fragment);
    }

    /// <summary>A file name that does not exist yet in the folder: name.ext, name-2.ext, …</summary>
    public static string Unique(string folder, string fileName, Func<string, bool> exists)
    {
        int dot = fileName.LastIndexOf('.');
        var stem = dot < 0 ? fileName : fileName[..dot];
        var ext = dot < 0 ? "" : fileName[dot..];
        var candidate = folder + fileName;
        for (int n = 2; exists(candidate); n++) candidate = $"{folder}{stem}-{n}{ext}";
        return candidate;
    }

    /// <summary>File-name friendly version of a title ("Nota del traductor" → "nota-del-traductor").</summary>
    public static string Slug(string s)
    {
        var folded = Text.TextNormalizer.Fold(s);
        var sb = new System.Text.StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "pagina" : slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
