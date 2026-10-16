using System.Xml;
using Kaku.Core.Epub;

namespace Kaku.Core.Structure;

public enum ProblemKind
{
    BrokenLink,
    MissingFragment,
    MissingFile,
    NotInManifest,
    Unused,
    InvalidXhtml,
    OrphanMarker,
    NoteWithoutMarker,
}

/// <summary>A problem found by <see cref="BookCheck"/>. Source/Line point to where it can be fixed.</summary>
public sealed record BookProblem(ProblemKind Kind, string Path, string Detail, string? Source = null, int Line = 0);

/// <summary>Consistency check of the package: links, manifest and unused files.</summary>
public static class BookCheck
{
    public static List<BookProblem> Run(Book book)
    {
        book.FlushChapters();
        var problems = new List<BookProblem>();
        var manifest = book.Opf.Manifest.Select(m => m.Path).ToHashSet(StringComparer.Ordinal);

        // manifest items without a file
        foreach (var m in book.Opf.Manifest.Where(m => !book.Package.Exists(m.Path)))
            problems.Add(new BookProblem(ProblemKind.MissingFile, m.Path, "En el manifiesto, pero el archivo no existe."));

        // files without a manifest item
        foreach (var e in book.Package.Entries)
        {
            // root-level files (rights.xml, iTunesMetadata.plist…) belong to stores/readers, not to the book
            if (!e.Path.Contains('/') || e.Path.StartsWith("META-INF/", StringComparison.Ordinal) || e.Path == book.Opf.Path) continue;
            if (!manifest.Contains(e.Path)) problems.Add(new BookProblem(ProblemKind.NotInManifest, e.Path, "El archivo está en el EPUB pero no en el manifiesto."));
        }

        foreach (var c in book.Chapters.Where(c => c.ParseError != null))
            problems.Add(new BookProblem(ProblemKind.InvalidXhtml, c.Path, c.ParseError!, c.Path, c.ParseErrorLine));

        // links
        var links = LinkIndex.Build(book);
        var idCache = new Dictionary<string, HashSet<string>?>();
        HashSet<string>? IdsOf(string path)
        {
            if (idCache.TryGetValue(path, out var ids)) return ids;
            ids = null;
            if (LinkIndex.IsMarkup(path) && book.Package.Read(path) is { } data)
            {
                try
                {
                    var doc = XmlLoader.Parse(book.ChapterByPath(path)?.SourceText ?? XmlLoader.DecodeText(data));
                    ids = doc.Descendants().Select(e => (string?)e.Attribute("id") ?? (string?)e.Attribute("name")).OfType<string>().ToHashSet();
                }
                catch (XmlException)
                {
                }
            }
            return idCache[path] = ids;
        }
        foreach (var l in links)
        {
            if (!book.Package.Exists(l.Target))
                problems.Add(new BookProblem(ProblemKind.BrokenLink, l.Target, $"Enlace roto «{l.Raw}»", l.Source, l.Line));
            else if (l.Fragment != null && IdsOf(l.Target) is { } ids && !ids.Contains(l.Fragment))
                problems.Add(new BookProblem(ProblemKind.MissingFragment, l.Target, $"No existe el destino «#{l.Fragment}» de «{l.Raw}»", l.Source, l.Line));
        }

        // notes: markers whose note is gone, notes nobody refers to
        var notes = Notes.NoteIndex.Build(book);
        foreach (var m in notes.OrphanMarkers)
            problems.Add(new BookProblem(ProblemKind.OrphanMarker, m.ChapterPath, $"Llamada a una nota que no existe ({m.Context})", m.ChapterPath, 0));
        foreach (var n in notes.Notes.Where(n => n.Markers.Count == 0))
            problems.Add(new BookProblem(ProblemKind.NoteWithoutMarker, n.NotePath ?? "", $"Nota sin llamada en el texto: «{Chapter.Shorten(n.Text, 60)}»", n.NotePath, 0));

        // unused: not in the spine, not the nav/ncx/cover, and nothing links to it
        var referenced = links.Select(l => l.Target).ToHashSet(StringComparer.Ordinal);
        var spine = book.Opf.Spine.Select(s => s.Item?.Path).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var m in book.Opf.Manifest)
        {
            if (spine.Contains(m.Path) || referenced.Contains(m.Path) || m.Path == book.Opf.NavPath || m.Path == book.Opf.NcxPath
                || m.Path == book.Opf.CoverImagePath || !book.Package.Exists(m.Path)) continue;
            problems.Add(new BookProblem(ProblemKind.Unused, m.Path, "Ningún archivo lo usa."));
        }
        return problems;
    }
}
