using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kaku.App.Localization;
using Kaku.Core.Epub;

namespace Kaku.App.Editor;

/// <summary>Views for non-chapter files: code (CSS, OPF, NCX, …), images and fonts.</summary>
public static class FileViews
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xhtml", ".html", ".htm", ".xml", ".opf", ".ncx", ".css", ".js", ".txt", ".svg", ".smil", ".json",
    };

    public static bool IsText(string path) =>
        TextExtensions.Contains(EpubPath.Extension(path)) || EpubPath.FileName(path) == "mimetype";

    public static bool IsRasterImage(string path) =>
        EpubPath.Extension(path) is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";

    public static FrameworkElement Create(EpubEntry entry, bool editable)
    {
        if (IsText(entry.Path))
        {
            var ed = EditorFactory.CreateCode(entry.Path);
            ed.Text = XmlLoader.DecodeText(entry.Data);
            ed.IsReadOnly = !editable;
            ed.Document.UndoStack.ClearAll();
            return ed;
        }
        if (IsRasterImage(entry.Path) && CreateImage(entry) is { } img) return img;
        return Info(entry, T.Get("File_NoPreview"));
    }

    private static FrameworkElement? CreateImage(EpubEntry entry)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(entry.Data);
            bmp.EndInit();
            bmp.Freeze();

            var panel = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)) };
            var info = new TextBlock
            {
                Text = T.F("File_ImageInfo", entry.Path, bmp.PixelWidth, bmp.PixelHeight, T.N0((entry.Data.Length + 1023) / 1024)),
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = Brushes.DimGray,
            };
            DockPanel.SetDock(info, Dock.Bottom);
            panel.Children.Add(info);
            var image = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            panel.Children.Add(image);
            return panel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FrameworkElement Info(EpubEntry entry, string message) => new TextBlock
    {
        Text = $"{entry.Path}\n{T.N0(entry.Data.Length)} bytes\n\n{message}",
        Margin = new Thickness(20),
        Foreground = Brushes.DimGray,
    };
}
