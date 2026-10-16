using System.Windows;
using Kaku.App.Editor;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Structure;

namespace Kaku.App.Views;

/// <summary>Title, authors, language, publisher, series, description and cover image.</summary>
public partial class MetadataDialog : Window
{
    private readonly Book _book;
    private readonly string? _originalCover;

    public MetadataDialog(Window owner, Book book, BookMetadata m)
    {
        InitializeComponent();
        Owner = owner;
        _book = book;
        TitleBox.Text = m.Title;
        AuthorsBox.Text = string.Join(Environment.NewLine, m.Creators);
        LanguageBox.Text = m.Language;
        PublisherBox.Text = m.Publisher;
        SeriesBox.Text = m.Series;
        SeriesIndexBox.Text = m.SeriesIndex;
        DescriptionBox.Text = m.Description;

        _originalCover = book.Opf.CoverImagePath;
        CoverBox.Items.Add(T.Get("Meta_NoCover"));
        foreach (var img in book.Opf.Manifest.Where(i => i.IsImage)) CoverBox.Items.Add(img.Path);
        CoverBox.SelectedItem = _originalCover != null && CoverBox.Items.Contains(_originalCover) ? _originalCover : CoverBox.Items[0];
        Loaded += (_, _) => TitleBox.Focus();
    }

    public BookMetadata Result => new(
        TitleBox.Text.Trim(),
        AuthorsBox.Text.Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
        LanguageBox.Text.Trim(),
        PublisherBox.Text.Trim(),
        DescriptionBox.Text.Trim(),
        SeriesBox.Text.Trim(),
        SeriesIndexBox.Text.Trim());

    /// <summary>New cover image path, or null if unchanged / none chosen.</summary>
    public string? NewCover => CoverBox.SelectedItem is string p && CoverBox.SelectedIndex > 0 && p != _originalCover ? p : null;

    private void OnCoverChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        CoverImage.Source = CoverBox.SelectedIndex > 0 && CoverBox.SelectedItem is string p ? Thumbnails.Load(_book.Package.Read(p), 520) : null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TitleBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, T.Get("Meta_NeedTitle"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
