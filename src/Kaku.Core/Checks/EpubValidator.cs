using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Structure;

namespace Kaku.Core.Checks;

/// <summary>
/// EPUB validity: the package checks of <see cref="BookCheck"/> plus the rules e-readers trip over
/// (required metadata, nav, spine, duplicate ids, media types, file names). <see cref="RunEpubcheck"/>
/// runs the official W3C epubcheck when Java and the jar are available.
/// </summary>
public static partial class EpubValidator
{
    public static List<Problem> Run(Book book)
    {
        var list = new List<Problem>();
        void Add(Severity sev, string msg, string? path = null, int line = 0) =>
            list.Add(new Problem(CheckKind.Epub, sev, msg, -1, -1, 0, 0, 0, Path: path ?? book.Opf.Path, Line: line));

        foreach (var p in BookCheck.Run(book))
        {
            var sev = p.Kind switch
            {
                ProblemKind.Unused => Severity.Info,
                ProblemKind.NotInManifest or ProblemKind.NoteWithoutMarker => Severity.Warning,
                _ => Severity.Error,
            };
            var label = p.Kind switch
            {
                ProblemKind.BrokenLink => "Enlace roto",
                ProblemKind.MissingFragment => "Destino inexistente",
                ProblemKind.MissingFile => "Falta un archivo del manifiesto",
                ProblemKind.NotInManifest => "Archivo fuera del manifiesto",
                ProblemKind.Unused => "Archivo sin usar",
                ProblemKind.InvalidXhtml => "XHTML no válido",
                ProblemKind.OrphanMarker => "Llamada a nota sin nota",
                ProblemKind.NoteWithoutMarker => "Nota sin llamada",
                _ => p.Kind.ToString(),
            };
            list.Add(new Problem(CheckKind.Epub, sev, $"{label}: {p.Detail} ({p.Path})", -1, -1, 0, 0, 0, Path: p.Source ?? p.Path, Line: p.Line));
        }

        // package document
        var opfText = XmlLoader.DecodeText(book.Package.Read(book.Opf.Path)!);
        var opf = XmlLoader.Parse(opfText);
        var root = opf.Root!;
        var meta = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? Dc(string n) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == n && e.Name.NamespaceName == "http://purl.org/dc/elements/1.1/")?.Value.Trim();
        if (string.IsNullOrEmpty(Dc("title"))) Add(Severity.Error, "El libro no tiene título (dc:title).");
        if (string.IsNullOrEmpty(Dc("language"))) Add(Severity.Error, "Falta el idioma del libro (dc:language).");
        else if (Dc("language") is "ja" && book.Chapters.Sum(c => c.WordCount) > 0 && !book.Chapters.Any(c => c.SearchText.Any(ch => ch is >= '぀' and <= 'ヿ')))
            Add(Severity.Warning, "El idioma del libro es «ja», pero el texto está traducido: el lector puede usar una fuente o un diccionario japoneses (Libro → Metadatos).");
        var uidRef = (string?)root.Attribute("unique-identifier");
        var ids = meta?.Elements().Where(e => e.Name.LocalName == "identifier").ToList() ?? [];
        if (ids.Count == 0) Add(Severity.Error, "Falta el identificador del libro (dc:identifier).");
        else if (uidRef != null && ids.All(i => (string?)i.Attribute("id") != uidRef))
            Add(Severity.Error, $"unique-identifier=\"{uidRef}\" no corresponde a ningún dc:identifier.");
        bool epub3 = book.Opf.Version.StartsWith('3');
        if (epub3)
        {
            if (meta?.Elements().Any(e => (string?)e.Attribute("property") == "dcterms:modified") != true)
                Add(Severity.Error, "EPUB 3 sin fecha de modificación (dcterms:modified).");
            if (book.Opf.NavPath == null) Add(Severity.Error, "EPUB 3 sin documento de navegación (nav).");
        }
        if (book.Opf.Spine.Count == 0) Add(Severity.Error, "El orden de lectura (spine) está vacío.");
        foreach (var s in book.Opf.Spine)
        {
            if (s.Item == null) Add(Severity.Error, $"El orden de lectura cita «{s.IdRef}», que no está en el manifiesto.");
            else if (!s.Item.IsXhtml && s.Item.MediaType != "image/svg+xml")
                Add(Severity.Warning, $"En el orden de lectura hay un archivo que no es XHTML: {s.Item.Path} ({s.Item.MediaType}).", s.Item.Path);
        }
        var manifestIds = book.Opf.Manifest.GroupBy(m => m.Id).Where(g => g.Count() > 1);
        foreach (var g in manifestIds) Add(Severity.Error, $"Id repetido en el manifiesto: «{g.Key}».");

        foreach (var m in book.Opf.Manifest)
        {
            var expected = BookStructure.MediaTypeFor(m.Path);
            if (expected == "text/javascript" && m.MediaType is "application/javascript" or "application/x-javascript" or "application/ecmascript") continue;
            if (expected != "application/octet-stream" && m.MediaType != expected && !(m.IsXhtml && expected == "application/xhtml+xml")
                && !(m.IsFont && expected.StartsWith("font/")))
                Add(Severity.Warning, $"Tipo de archivo «{m.MediaType}» no coincide con la extensión de {EpubPath.FileName(m.Path)} (se esperaba {expected}).");
            if (m.Path.Any(c => c == ' ' || c > 127))
                Add(Severity.Info, $"Nombre de archivo con espacios o caracteres especiales: {m.Path} (algunos lectores fallan; Archivos → Renombrar).", m.Path);
        }

        // documents: duplicate ids, images without alt
        foreach (var c in book.Chapters.Where(c => c.Document != null && c.ParseError == null))
        {
            var dup = c.Document!.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>().GroupBy(x => x).Where(g => g.Count() > 1);
            foreach (var g in dup) Add(Severity.Error, $"Id repetido «{g.Key}» en {EpubPath.FileName(c.Path)}.", c.Path, LineOf(c.Document, g.Key));
            foreach (var img in c.Document.Descendants().Where(e => e.Name.LocalName == "img" && e.Attribute("alt") == null))
                Add(Severity.Info, $"Imagen sin atributo alt en {EpubPath.FileName(c.Path)}.", c.Path, ((IXmlLineInfo)img).LineNumber);
        }
        return list;
    }

    private static int LineOf(XDocument doc, string id) =>
        doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == id) is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

    // ------------------------------------------------------------------ epubcheck

    /// <summary>java.exe from JAVA_HOME or the PATH, or null.</summary>
    public static string? FindJava()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (home != null && File.Exists(Path.Combine(home, "bin", "java.exe"))) return Path.Combine(home, "bin", "java.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "java.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    [GeneratedRegex(@"^(?<sev>FATAL|ERROR|WARNING|USAGE|INFO)\((?<code>[A-Z]+-\d+\w*)\): (?<file>.*?\.epub)(?:/(?<path>[^(:]+))?(?:\((?<line>-?\d+),(?<col>-?\d+)\))?: (?<msg>.*)$")]
    private static partial Regex EpubcheckLineRe();

    /// <summary>Runs epubcheck on a copy of the book as it is now (saved or not).</summary>
    public static async Task<(List<Problem> Problems, string? Error)> RunEpubcheck(Book book, string java, string jar, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kaku-epubcheck-{Guid.NewGuid():N}.epub");
        try
        {
            book.FlushChapters();
            book.Package.Save(tmp); // a copy: does not mark the book as saved
            var psi = new ProcessStartInfo(java)
            {
                ArgumentList = { "-jar", jar, tmp },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var lines = ((await outTask) + "\n" + (await errTask)).Split('\n').Select(l => l.TrimEnd('\r'));
            var problems = new List<Problem>();
            foreach (var l in lines)
            {
                var m = EpubcheckLineRe().Match(l);
                if (!m.Success) continue;
                var sev = m.Groups["sev"].Value switch { "FATAL" or "ERROR" => Severity.Error, "WARNING" => Severity.Warning, _ => Severity.Info };
                int.TryParse(m.Groups["line"].Value, out var line);
                var path = m.Groups["path"].Success ? m.Groups["path"].Value : null;
                problems.Add(new Problem(CheckKind.Epub, sev, $"epubcheck {m.Groups["code"].Value}: {m.Groups["msg"].Value}", -1, -1, 0, 0, 0,
                    Path: path, Line: Math.Max(0, line)));
            }
            if (problems.Count == 0 && p.ExitCode != 0 && !lines.Any(l => l.Contains("No errors or warnings", StringComparison.OrdinalIgnoreCase)))
                return (problems, string.Join("\n", lines.Where(l => l.Length > 0).Take(8)));
            return (problems, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ([], ex.Message);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
