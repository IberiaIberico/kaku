using System.IO;
using System.Windows;
using System.Windows.Media;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Reference;
using Microsoft.Win32;

namespace Kaku.App.Views;

/// <summary>Choose the original (Japanese) EPUB of a book: suggested candidates, or any file.</summary>
public partial class ReferenceDialog : Window
{
    public sealed record Row(ReferenceCandidate Candidate)
    {
        public string Path => Candidate.Path;
        public string File => System.IO.Path.GetFileName(Candidate.Path) + (Candidate.Language.Length > 0 ? $"  ·  {Candidate.Language}" : "");
        public string Title => Candidate.Title.Length > 0 ? Candidate.Title : System.IO.Path.GetFileNameWithoutExtension(Candidate.Path);
        // file paths count up to 1, identical images up to 2, the original language 0.5
        public string MatchText => Candidate.Score >= 2.5 ? T.Get("Original_MatchHigh") : Candidate.Score >= 1.2 ? T.Get("Original_MatchMedium") : T.Get("Original_MatchLow");
        public Brush MatchBrush => Candidate.Score >= 2.5 ? Theme.Brush("ScoreGood") : Candidate.Score >= 1.2 ? Theme.Brush("Caution") : Theme.Brush("Faint");
    }

    public ReferenceDialog(Window owner, Book book, string? current, IReadOnlyList<string> folders)
    {
        InitializeComponent();
        Owner = owner;
        CurrentText.Text = current == null ? T.Get("Original_NoneLinked") : T.F("Original_Current", System.IO.Path.GetFileName(current));
        RemoveButton.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            var list = await Task.Run(() => ReferenceBook.FindCandidates(book, folders));
            List.ItemsSource = list.Select(c => new Row(c)).ToList();
            SearchingText.Text = list.Count == 0 ? T.Get("Original_NoCandidates") : T.F("Original_Found", list.Count);
            if (list.Count > 0) List.SelectedIndex = 0;
        };
    }

    /// <summary>The chosen file; null with <see cref="Removed"/> false means "cancel".</summary>
    public string? Chosen { get; private set; }

    public bool Removed { get; private set; }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = List.SelectedItem != null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Row r) return;
        Chosen = r.Path;
        DialogResult = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T.Get("Dialog_EpubFilter"), Title = T.Get("Original_DialogTitle") };
        if (dlg.ShowDialog(this) != true) return;
        Chosen = dlg.FileName;
        DialogResult = true;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        Removed = true;
        DialogResult = true;
    }
}
