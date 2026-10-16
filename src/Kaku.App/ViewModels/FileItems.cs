using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Epub;

namespace Kaku.App.ViewModels;

public sealed record FileItem(string Path, string Name, string Detail, bool IsSpine)
{
    public string ToolTipText => $"{Path}\n{Detail}";
}

public sealed record FileGroup(string Name, List<FileItem> Items)
{
    public string Header => $"{Name} ({Items.Count})";
}

/// <summary>Builds the "Archivos" tree: every file of the EPUB grouped by type, text in reading order.</summary>
public static class FileTreeBuilder
{
    public static List<FileGroup> Build(Book book)
    {
        var manifest = book.Opf.Manifest.ToDictionary(m => m.Path, m => m, StringComparer.Ordinal);
        var spineOrder = book.Chapters.Select((c, i) => (c.Path, i)).ToDictionary(x => x.Path, x => x.i);

        var text = new List<FileItem>();
        var images = new List<FileItem>();
        var styles = new List<FileItem>();
        var fonts = new List<FileItem>();
        var other = new List<FileItem>();

        foreach (var e in book.Package.Entries)
        {
            manifest.TryGetValue(e.Path, out var item);
            var size = T.F("File_Size", T.N0((e.Data.Length + 1023) / 1024));
            var fi = new FileItem(e.Path, EpubPath.FileName(e.Path),
                item == null ? size : $"{item.MediaType} · {size}", spineOrder.ContainsKey(e.Path));
            if (item is { IsXhtml: true } && e.Path != book.Opf.NavPath) text.Add(fi);
            else if (item is { IsImage: true } || item == null && Editor.FileViews.IsRasterImage(e.Path)) images.Add(fi);
            else if (item is { IsCss: true } || EpubPath.Extension(e.Path) == ".css") styles.Add(fi);
            else if (item is { IsFont: true }) fonts.Add(fi);
            else other.Add(fi);
        }

        text.Sort((a, b) => Order(a).CompareTo(Order(b)));
        int Order(FileItem f) => spineOrder.TryGetValue(f.Path, out var i) ? i : int.MaxValue;

        var groups = new List<FileGroup>
        {
            new(T.Get("Files_Text"), text),
            new(T.Get("Files_Images"), images),
            new(T.Get("Files_Styles"), styles),
            new(T.Get("Files_Fonts"), fonts),
            new(T.Get("Files_Other"), other),
        };
        return groups.Where(g => g.Items.Count > 0).ToList();
    }
}
