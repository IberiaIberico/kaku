using System.Windows;
using System.Windows.Input;
using Kaku.App.Localization;
using Kaku.Core.Notes;
using Kaku.Core.Text;

namespace Kaku.App.Views;

/// <summary>Write a note (**negrita**, *cursiva*), or point the new marker to an existing note.</summary>
public partial class NoteDialog : Window
{
    private readonly List<BookNote> _notes;

    /// <param name="existing">The note being edited (null = new marker).</param>
    public NoteDialog(Window owner, string where, string word, List<BookNote> notes, BookNote? existing)
    {
        InitializeComponent();
        Owner = owner;
        _notes = notes;
        WhereText.Text = where;
        if (existing != null)
        {
            Title = T.F("Note_EditTitle", existing.Number);
            TextBox.Text = MarkdownLite.FromXhtml(existing.ContentXhtml);
            ReusePanel.Visibility = Visibility.Collapsed;
            NewRadio.Visibility = Visibility.Collapsed;
        }
        else
        {
            // notes that already explain this word come first (F38: reuse instead of writing it twice)
            var w = TextNormalizer.Fold(word);
            var ordered = notes.OrderByDescending(n => w.Length >= 3 && (TextNormalizer.Fold(n.Text).Contains(w) || n.Markers.Any(m => TextNormalizer.Fold(m.Context).EndsWith(w))))
                .ThenBy(n => n.Number).ToList();
            foreach (var n in ordered) ReuseBox.Items.Add(new ReuseItem(n));
            var suggested = ordered.FirstOrDefault(n => w.Length >= 3 && (TextNormalizer.Fold(n.Text).Contains(w) || n.Markers.Any(m => TextNormalizer.Fold(m.Context).EndsWith(w))));
            if (suggested != null)
            {
                ReuseBox.SelectedIndex = 0;
                SuggestText.Text = T.F("Note_Suggest", word);
            }
            else if (ReuseBox.Items.Count > 0) ReuseBox.SelectedIndex = 0;
            ReusePanel.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Loaded += (_, _) =>
        {
            TextBox.Focus();
            TextBox.CaretIndex = TextBox.Text.Length;
        };
    }

    private sealed record ReuseItem(BookNote Note)
    {
        public override string ToString()
        {
            var t = Note.Text;
            return $"{Note.Number}. {(t.Length > 80 ? t[..80] + "…" : t)}";
        }
    }

    /// <summary>Note text as XHTML (null when reusing).</summary>
    public string? ContentXhtml => ReuseRadio.IsChecked == true ? null : MarkdownLite.ToXhtml(TextBox.Text);

    public int? ReuseUid => ReuseRadio.IsChecked == true && ReuseBox.SelectedItem is ReuseItem r ? r.Note.Uid : null;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool reuse = ReuseRadio.IsChecked == true;
        TextBox.IsEnabled = !reuse;
        ReuseBox.IsEnabled = reuse;
    }

    private void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Intro accepts (Intro alone is a new line in the note)
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnOk(sender, e);
            e.Handled = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ReuseRadio.IsChecked != true && TextBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, T.Get("Note_Empty"), "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
