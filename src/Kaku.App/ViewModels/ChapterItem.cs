using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Text;

namespace Kaku.App.ViewModels;

public sealed record HeadingItem(ChapterItem Owner, string Title, int BlockIndex, int Level)
{
    public double Indent => 6 + Math.Max(0, Level - 1) * 12;
}

/// <summary>One row of the chapter browser.</summary>
public sealed partial class ChapterItem : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ChapterItem(Book book, Chapter chapter)
    {
        Book = book;
        Chapter = chapter;
        Headings = BuildHeadings();
        TitleFolded = TextNormalizer.Fold(chapter.Title);
    }

    // a heading that just repeats the chapter title adds nothing to the tree
    private List<HeadingItem> BuildHeadings() => Chapter.Headings
        .Where(h => !(h.BlockIndex <= 2 && TextNormalizer.Fold(h.Title) == TextNormalizer.Fold(Chapter.Title)))
        .Select(h => new HeadingItem(this, h.Title, h.BlockIndex, h.Level)).ToList();

    /// <summary>After edits or a save: re-read title, headings, positions and badges.</summary>
    public void RefreshState()
    {
        Headings = BuildHeadings();
        TitleFolded = TextNormalizer.Fold(Chapter.Title);
        OnPropertyChanged(string.Empty);
    }

    public bool IsModified => Chapter.IsModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarks), nameof(BookmarksText))]
    private int _bookmarkCount;

    public bool HasBookmarks => BookmarkCount > 0;
    public string BookmarksText => $"⚑ {BookmarkCount}";

    public Book Book { get; }
    public Chapter Chapter { get; }
    public List<HeadingItem> Headings { get; private set; }
    public string TitleFolded { get; private set; }

    // ---- tree: a main chapter with its continuation files / nested TOC entries below it
    public ChapterItem? Parent { get; set; }
    public List<ChapterItem> Children { get; } = new();
    public double Indent => Chapter.Depth * 16;
    public bool HasChildren => Children.Count > 0;
    public bool CanExpand => HasChildren || HasHeadings;

    public IEnumerable<ChapterItem> Ancestors
    {
        get
        {
            for (var p = Parent; p != null; p = p.Parent) yield return p;
        }
    }

    public IEnumerable<ChapterItem> Descendants => Children.SelectMany(c => c.Descendants.Prepend(c));

    /// <summary>Shown in the browser when no filter is active: every ancestor is expanded.</summary>
    public bool IsVisibleInTree => Ancestors.All(a => a.IsExpanded);

    /// <summary>Words of this chapter plus everything nested under it.</summary>
    public long GroupWordCount => Chapter.WordCount + Descendants.Sum(d => (long)d.Chapter.WordCount);

    public string NumberText => (Chapter.Index + 1).ToString(T.Es);
    public string Title => Chapter.Title;
    public string Snippet => Chapter.IsImageOnly ? T.Get("Chapter_ImagePage")
        : Chapter.WordCount < 20 && Children.FirstOrDefault(c => c.Chapter.WordCount >= 20) is { } body ? body.Chapter.Snippet // title page: show where the text starts
        : Chapter.Snippet;
    public string PositionText => T.F("Chapter_Position", T.N1(Book.PercentAt(Chapter.WordStart)), T.N0(GroupWordCount));
    public string ChildCountText => HasChildren ? T.F("Chapter_Parts", Children.Count + 1) : "";
    public bool HasHeadings => Headings.Count > 0;
    public bool HasNotes => Chapter.NoteRefCount > 0;
    public string NotesText => $"● {Chapter.NoteRefCount}";
    public bool HasError => Chapter.ParseError != null;
    public string ErrorText => T.F("Chapter_ParseError", Chapter.ParseError);
    public bool IsNonLinear => !Chapter.Linear;
    public bool HasThumbnail => Chapter.IsImageOnly && Chapter.FirstImagePath != null;

    public string ToolTipText
    {
        get
        {
            var text = string.Join("\n", Chapter.Blocks.Where(b => b.WordCount > 0).Take(8).Select(b => b.WordText.Trim()));
            if (text.Length > 600) text = text[..600] + "…";
            return $"{Chapter.Title}\n{Chapter.Path}\n\n{text}";
        }
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = LoadThumbnail();
            }
            return _thumbnail;
        }
    }

    private ImageSource? LoadThumbnail()
    {
        if (!HasThumbnail || Book.Package.Read(Chapter.FirstImagePath!) is not { } data) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 64;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildCount))]
    private bool _isExpanded;

    /// <summary>"3 partes" is only useful while the parts are folded away.</summary>
    public bool ShowChildCount => HasChildren && !IsExpanded;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>The current chapter is nested inside this collapsed item.</summary>
    [ObservableProperty]
    private bool _containsCurrent;

    /// <summary>Cursor position inside the chapter, 0–100 (shown as a bar on the current chapter).</summary>
    [ObservableProperty]
    private double _cursorPercent;

    public bool Matches(string foldedQuery) =>
        foldedQuery.Length == 0 || TitleFolded.Contains(foldedQuery, StringComparison.Ordinal)
                                || Chapter.SearchText.Contains(foldedQuery, StringComparison.Ordinal);
}
