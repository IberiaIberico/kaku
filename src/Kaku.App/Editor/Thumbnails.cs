using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kaku.App.Editor;

public static class Thumbnails
{
    /// <summary>Decoded image (or null for formats WPF cannot read, e.g. SVG).</summary>
    public static BitmapSource? Load(byte[]? data, int decodeHeight = 0)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pixel size without keeping a full decode around.</summary>
    public static (int Width, int Height)? Size(byte[]? data)
    {
        if (data == null) return null;
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
