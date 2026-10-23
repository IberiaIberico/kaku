using System.Windows;
using Kaku.App.Services;

namespace Kaku.App;

public partial class MainWindow
{
    private void InitTheme()
    {
        UpdateThemeMenu();
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeMenuClick(object sender, RoutedEventArgs e)
    {
        _settings.Theme = sender == ThemeDarkMenu ? ThemeChoice.Dark : sender == ThemeSystemMenu ? ThemeChoice.System : ThemeChoice.Light;
        UpdateThemeMenu();
        Theme.Apply(_settings.Theme);
        _settings.Save();
    }

    private void UpdateThemeMenu()
    {
        ThemeLightMenu.IsChecked = _settings.Theme == ThemeChoice.Light;
        ThemeDarkMenu.IsChecked = _settings.Theme == ThemeChoice.Dark;
        ThemeSystemMenu.IsChecked = _settings.Theme == ThemeChoice.System;
    }

    /// <summary>Repaints what does not follow DynamicResource: editor renderers and the previews.</summary>
    private void OnThemeChanged()
    {
        foreach (var t in _tabs)
        {
            t.Editor?.Redraw();
            if (t.View is ICSharpCode.AvalonEdit.TextEditor code) code.TextArea.TextView.Redraw();
        }
        Preview.ApplyTheme();
        OriginalPreview.ApplyTheme();
    }
}
