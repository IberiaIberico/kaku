using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Preview;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Kaku.App.Views;

/// <summary>
/// Rendered chapter preview. The book is served from memory at https://kaku.book/&lt;zip path&gt;,
/// so relative links, CSS, fonts and images work exactly as inside the EPUB.
/// </summary>
public sealed class PreviewPane : Grid
{
    private const string Host = "kaku.book";
    private readonly WebView2 _web = new();
    private readonly TextBlock _message = new()
    {
        Margin = new Thickness(16),
        Foreground = Brushes.DimGray,
        TextWrapping = TextWrapping.Wrap,
    };
    private Book? _book;
    private string? _currentPath;
    private int _pendingBlock = -1;
    private bool _ready;
    private (Chapter Chapter, int Block)? _queued;
    private string _emptyText = "";

    /// <summary>Force horizontal writing (used for the Japanese original).</summary>
    public bool ForceHorizontal { get; set; }

    /// <summary>Text shown while there is nothing to preview.</summary>
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            _emptyText = value;
            if (_book == null) _message.Text = value;
        }
    }

    /// <summary>Shows the message instead of a page (no counterpart in the original, for example).</summary>
    public void ShowMessage(string text)
    {
        _currentPath = null;
        _web.Visibility = Visibility.Hidden;
        _message.Visibility = Visibility.Visible;
        _message.Text = text;
    }

    public PreviewPane()
    {
        Children.Add(_web);
        Children.Add(_message);
        _message.Text = T.Get("Preview_Empty");
        _web.Visibility = Visibility.Hidden;
        _emptyText = _message.Text;
        Loaded += async (_, _) => await InitAsync();
    }

    /// <summary>The user clicked a paragraph in the preview.</summary>
    public event Action<int>? BlockClicked;

    /// <summary>The preview navigated to a chapter by itself (a link was followed).</summary>
    public event Action<string>? ChapterLoaded;

    /// <summary>A Kaku shortcut was pressed while the preview had focus (key, shift).</summary>
    public event Action<string, bool, bool, bool>? ShortcutPressed;

    private async Task InitAsync()
    {
        if (_ready || _web.CoreWebView2 != null) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            _message.Text = T.F("Preview_Unavailable", ex.Message);
            return;
        }
        var core = _web.CoreWebView2!;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        _ready = true;
        if (_queued is { } q)
        {
            _queued = null;
            ShowChapter(q.Chapter, q.Block);
        }
    }

    public void SetBook(Book? book)
    {
        _book = book;
        _currentPath = null;
        if (book == null)
        {
            _web.Visibility = Visibility.Hidden;
            _message.Visibility = Visibility.Visible;
            _message.Text = _emptyText;
            if (_ready) _web.CoreWebView2.Navigate("about:blank");
        }
    }

    /// <summary>Shows a chapter (or just scrolls, if it is already shown) and highlights a block.</summary>
    public void ShowChapter(Chapter chapter, int block)
    {
        if (!_ready)
        {
            _queued = (chapter, block);
            return;
        }
        _web.Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Collapsed;
        if (_currentPath == chapter.Path)
        {
            ScrollToBlock(block);
            return;
        }
        _currentPath = chapter.Path;
        _pendingBlock = block;
        _web.CoreWebView2.Navigate($"https://{Host}/{EscapePath(chapter.Path)}");
    }

    public string? CurrentPath => _currentPath;

    /// <summary>Re-renders the chapter (after structural edits or CSS changes), keeping the block in view.</summary>
    public void Reload(Chapter chapter, int block)
    {
        _currentPath = null;
        ShowChapter(chapter, block);
    }

    /// <summary>Replaces the content of one block in place: instant preview while typing.</summary>
    public void ReplaceBlock(int block, string innerXhtml)
    {
        if (!_ready || block < 0) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "replace", index = block, html = innerXhtml }));
    }

    /// <summary>Records which chapter the preview shows without navigating (used when the preview navigated itself).</summary>
    public void SetCurrentPath(string path) => _currentPath = path;

    public void ScrollToBlock(int block)
    {
        if (!_ready || block < 0) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scrollTo", index = block }));
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "block":
                BlockClicked?.Invoke(root.GetProperty("index").GetInt32());
                break;
            case "loaded":
                var path = root.GetProperty("path").GetString() ?? "";
                if (path != _currentPath)
                {
                    _currentPath = path;
                    ChapterLoaded?.Invoke(path);
                }
                if (_pendingBlock >= 0)
                {
                    ScrollToBlock(_pendingBlock);
                    _pendingBlock = -1;
                }
                break;
            case "key":
                ShortcutPressed?.Invoke(root.GetProperty("key").GetString() ?? "", root.GetProperty("shift").GetBoolean(),
                    root.TryGetProperty("ctrl", out var ctrl) && ctrl.GetBoolean(), root.TryGetProperty("alt", out var alt) && alt.GetBoolean());
                break;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank") return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        byte[]? data = null;
        string mime = "application/octet-stream";

        if (path == PreviewBuilder.ScriptPath || path == PreviewBuilder.StylePath)
        {
            var name = path == PreviewBuilder.ScriptPath ? "Kaku.preview.js" : "Kaku.preview.css";
            using var ms = new MemoryStream();
            using (var s = typeof(PreviewPane).Assembly.GetManifestResourceStream(name)!) s.CopyTo(ms);
            // the stylesheet is loaded after the book's own CSS, so it can override the writing mode
            if (path == PreviewBuilder.StylePath && ForceHorizontal)
                using (var h = typeof(PreviewPane).Assembly.GetManifestResourceStream("Kaku.horizontal.css")!) h.CopyTo(ms);
            data = ms.ToArray();
            mime = path == PreviewBuilder.ScriptPath ? "text/javascript" : "text/css";
        }
        else if (_book != null)
        {
            var chapter = _book.ChapterByPath(path);
            if (chapter != null)
            {
                data = PreviewBuilder.Build(chapter, chapter.SourceText);
                mime = chapter.Item.MediaType == "text/html" ? "text/html" : "application/xhtml+xml";
            }
            else if (_book.Package.Find(path) is { } entry)
            {
                data = entry.Data;
                mime = _book.Opf.ItemByPath(entry.Path)?.MediaType is { Length: > 0 } mt ? mt : GuessMime(path);
            }
        }

        var env = _web.CoreWebView2.Environment;
        e.Response = data == null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(new MemoryStream(data), 200, "OK", $"Content-Type: {mime}\r\nCache-Control: no-store");
    }

    private static string GuessMime(string path) => EpubPath.Extension(path) switch
    {
        ".xhtml" => "application/xhtml+xml",
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
