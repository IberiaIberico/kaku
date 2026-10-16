using System.IO;
using System.Windows;
using Kaku.App.Localization;
using Kaku.Core.Epub;
using Kaku.Core.Structure;
using Microsoft.Win32;

namespace Kaku.App.Views;

public enum PagePosition { AfterCurrent, BeforeCurrent, End }

/// <summary>New page from a template, or from an imported TXT / Markdown / HTML file.</summary>
public partial class AddPageDialog : Window
{
    private string? _importedBody;
    private bool _titleEdited;

    public AddPageDialog(Window owner, bool hasCurrentChapter, bool import)
    {
        InitializeComponent();
        Owner = owner;
        if (!hasCurrentChapter)
        {
            AfterRadio.IsEnabled = BeforeRadio.IsEnabled = false;
            EndRadio.IsChecked = true;
        }
        TitleBox.Text = T.Get("AddPage_DefaultTitle");
        TitleBox.TextChanged += (_, _) => _titleEdited = true;
        Loaded += (_, _) =>
        {
            if (import) OnImport(this, new RoutedEventArgs());
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    public string PageTitle => TitleBox.Text.Trim().Length > 0 ? TitleBox.Text.Trim() : T.Get("AddPage_DefaultTitle");
    public PagePosition Position => BeforeRadio.IsChecked == true ? PagePosition.BeforeCurrent : EndRadio.IsChecked == true ? PagePosition.End : PagePosition.AfterCurrent;
    public bool AddToToc => TocCheck.IsChecked == true;

    public string Body => _importedBody ?? BookStructure.TemplateBody(TypeBox.SelectedIndex switch
    {
        1 => PageTemplate.TranslatorNote,
        2 => PageTemplate.Characters,
        _ => PageTemplate.Blank,
    }, PageTitle);

    private void OnTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _titleEdited) return;
        TitleBox.Text = TypeBox.SelectedIndex switch
        {
            1 => T.Get("AddPage_Note"),
            2 => T.Get("AddPage_Characters"),
            _ => T.Get("AddPage_DefaultTitle"),
        };
        _titleEdited = false;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T.Get("AddPage_ImportFilter"), Title = T.Get("AddPage_Import") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = XmlLoader.DecodeText(File.ReadAllBytes(dlg.FileName));
            var (title, body) = BookStructure.ImportBody(dlg.FileName, text);
            _importedBody = body;
            TitleBox.Text = title;
            _titleEdited = true;
            TypeBox.IsEnabled = false;
            ImportedText.Text = T.F("AddPage_Imported", Path.GetFileName(dlg.FileName), body.Length);
            ImportedText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kaku", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
