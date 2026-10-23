using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kaku.App.Editor;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Epub;
using Kaku.Core.Structure;

namespace Kaku.App.Views;

public sealed class GalleryItem
{
    public required string Path { get; init; }
    public string Name => EpubPath.FileName(Path);
    public ImageSource? Thumbnail { get; init; }
    public bool NoPreview => Thumbnail == null;
    public bool IsCover { get; init; }
    public string InfoText { get; init; } = "";
    public List<LinkRef> Usages { get; init; } = new();
    public string UsageText => Usages.Count == 0 ? T.Get("Gallery_Unused") : T.F("Gallery_UsedIn", Usages.Select(u => u.Source).Distinct().Count());
    public Brush UsageBrush => Usages.Count == 0 ? Theme.Brush("Caution") : Theme.Brush("Faint");
}

public enum GalleryAction { Add, Insert, Replace, Cover, Delete }

/// <summary>All images of the book: preview, add, replace, delete, set as cover, insert into the text, see where used.</summary>
public partial class GalleryView : UserControl
{
    private Book? _book;

    public GalleryView()
    {
        InitializeComponent();
        UpdateButtons();
    }

    /// <summary>The window performs actions (it owns the undo history). Item is null for Add.</summary>
    public event Action<GalleryAction, GalleryItem?>? ActionRequested;

    /// <summary>Open the file that uses the image (path, line).</summary>
    public event Action<string, int>? UsageRequested;

    public GalleryItem? Selected => Items.SelectedItem as GalleryItem;

    /// <summary>Picker mode (dialog): no destructive actions; double-click = insert.</summary>
    public bool PickerMode
    {
        set
        {
            var v = value ? Visibility.Collapsed : Visibility.Visible;
            ReplaceButton.Visibility = CoverButton.Visibility = DeleteButton.Visibility = UsagesButton.Visibility = v;
        }
    }

    public void Load(Book book, string? select = null)
    {
        _book = book;
        var usages = LinkIndex.Build(book).GroupBy(l => l.Target).ToDictionary(g => g.Key, g => g.ToList());
        var keep = select ?? Selected?.Path;
        var items = book.Opf.Manifest.Where(m => m.IsImage && book.Package.Exists(m.Path)).Select(m =>
        {
            var data = book.Package.Read(m.Path);
            var size = Thumbnails.Size(data);
            var kb = T.N0(((data?.Length ?? 0) + 1023) / 1024);
            return new GalleryItem
            {
                Path = m.Path,
                Thumbnail = Thumbnails.Load(data, 320),
                IsCover = m.Path == book.Opf.CoverImagePath,
                InfoText = size is { } s ? $"{s.Width} × {s.Height} px · {kb} KB" : $"{kb} KB",
                Usages = usages.GetValueOrDefault(m.Path) ?? new(),
            };
        }).ToList();
        Items.ItemsSource = items;
        Items.SelectedItem = items.FirstOrDefault(i => i.Path == keep) ?? items.FirstOrDefault();
        CountText.Text = T.F("Gallery_Count", items.Count, T.N0((items.Sum(i => (long)(book.Package.Read(i.Path)?.Length ?? 0)) + 1023) / 1024));
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = Selected != null;
        InsertButton.IsEnabled = ReplaceButton.IsEnabled = CoverButton.IsEnabled = DeleteButton.IsEnabled = has;
        UsagesButton.IsEnabled = has && Selected!.Usages.Count > 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnAdd(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(GalleryAction.Add, null);

    private void OnInsert(object sender, RoutedEventArgs e) => Act(GalleryAction.Insert);

    private void OnReplace(object sender, RoutedEventArgs e) => Act(GalleryAction.Replace);

    private void OnCover(object sender, RoutedEventArgs e) => Act(GalleryAction.Cover);

    private void OnDelete(object sender, RoutedEventArgs e) => Act(GalleryAction.Delete);

    private void Act(GalleryAction a)
    {
        if (Selected is { } item) ActionRequested?.Invoke(a, item);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Act(GalleryAction.Insert);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Act(GalleryAction.Insert); e.Handled = true; }
        else if (e.Key == Key.Delete && DeleteButton.Visibility == Visibility.Visible) { Act(GalleryAction.Delete); e.Handled = true; }
    }

    private void OnUsages(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _book == null) return;
        var menu = new ContextMenu { PlacementTarget = UsagesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var u in item.Usages)
        {
            var c = _book.ChapterByPath(u.Source);
            var mi = new MenuItem { Header = (c != null ? $"{c.Index + 1}. {c.Title}" : u.Source) + $" · {T.F("Search_PositionLine", u.Line)}" };
            mi.Click += (_, _) => UsageRequested?.Invoke(u.Source, u.Line);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
