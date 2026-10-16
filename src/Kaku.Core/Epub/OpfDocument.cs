using System.Xml.Linq;

namespace Kaku.Core.Epub;

public sealed record ManifestItem(string Id, string Path, string Href, string MediaType, string Properties)
{
    public bool HasProperty(string p) => Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(p);
    public bool IsXhtml => MediaType is "application/xhtml+xml" or "text/html";
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.Ordinal);
    public bool IsCss => MediaType == "text/css";
    public bool IsFont => MediaType.StartsWith("font/", StringComparison.Ordinal) || MediaType.Contains("font", StringComparison.Ordinal)
                          || EpubPath.Extension(Path) is ".ttf" or ".otf" or ".woff" or ".woff2";
}

public sealed record SpineItem(string IdRef, bool Linear, ManifestItem? Item);

/// <summary>The package document (.opf): metadata, manifest and spine.</summary>
public sealed class OpfDocument
{
    public required string Path { get; init; }
    public string Version { get; init; } = "";
    public List<ManifestItem> Manifest { get; } = new();
    public List<SpineItem> Spine { get; } = new();
    public string Title { get; set; } = "";
    public List<string> Creators { get; } = new();
    public string Language { get; set; } = "";
    public string? NavPath { get; set; }
    public string? NcxPath { get; set; }
    public string? CoverImagePath { get; set; }

    public ManifestItem? ItemByPath(string path) =>
        Manifest.FirstOrDefault(m => m.Path == path) ?? Manifest.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the OPF path from META-INF/container.xml.</summary>
    public static string FindOpfPath(EpubPackage pkg)
    {
        var container = pkg.Read(EpubPackage.ContainerPath);
        if (container != null)
        {
            var doc = XmlLoader.Parse(container);
            var rootfile = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile"
                && (XmlLoader.AttrLocal(e, "media-type") ?? "application/oebps-package+xml") == "application/oebps-package+xml");
            var fp = rootfile == null ? null : XmlLoader.AttrLocal(rootfile, "full-path");
            if (!string.IsNullOrEmpty(fp)) return EpubPath.Normalize(Uri.UnescapeDataString(fp));
        }
        return pkg.Entries.FirstOrDefault(e => e.Path.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.Path
               ?? throw new InvalidDataException("El EPUB no contiene un documento OPF.");
    }

    public static OpfDocument Load(EpubPackage pkg)
    {
        var path = FindOpfPath(pkg);
        var data = pkg.Read(path) ?? throw new InvalidDataException($"No se encuentra {path}.");
        return Parse(path, XmlLoader.Parse(data));
    }

    public static OpfDocument Parse(string path, XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("OPF vacío.");
        var opf = new OpfDocument { Path = path, Version = (string?)root.Attribute("version") ?? "" };

        foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var id = (string?)item.Attribute("id") ?? "";
            var href = (string?)item.Attribute("href") ?? "";
            if (href.Length == 0) continue;
            opf.Manifest.Add(new ManifestItem(id, EpubPath.Resolve(path, href), href,
                ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant(),
                (string?)item.Attribute("properties") ?? ""));
        }

        var byId = opf.Manifest.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var spine = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spine != null)
        {
            foreach (var ir in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = (string?)ir.Attribute("idref") ?? "";
                opf.Spine.Add(new SpineItem(idref, (string?)ir.Attribute("linear") != "no", byId.GetValueOrDefault(idref)));
            }
            var tocId = (string?)spine.Attribute("toc");
            if (tocId != null && byId.TryGetValue(tocId, out var ncx)) opf.NcxPath = ncx.Path;
        }
        opf.NcxPath ??= opf.Manifest.FirstOrDefault(m => m.MediaType == "application/x-dtbncx+xml")?.Path;
        opf.NavPath = opf.Manifest.FirstOrDefault(m => m.HasProperty("nav"))?.Path;

        var metadata = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata != null)
        {
            opf.Title = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() ?? "";
            opf.Creators.AddRange(metadata.Elements().Where(e => e.Name.LocalName == "creator").Select(e => e.Value.Trim()).Where(s => s.Length > 0));
            opf.Language = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Value.Trim() ?? "";
            var coverMeta = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover");
            var coverId = (string?)coverMeta?.Attribute("content");
            opf.CoverImagePath = opf.Manifest.FirstOrDefault(m => m.HasProperty("cover-image"))?.Path
                                 ?? (coverId != null && byId.TryGetValue(coverId, out var c) ? c.Path : null);
        }
        return opf;
    }
}
