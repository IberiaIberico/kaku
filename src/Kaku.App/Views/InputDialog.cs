using System.Windows;
using System.Windows.Controls;
using Kaku.App.Localization;

namespace Kaku.App.Views;

/// <summary>A one-line text prompt.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = T.Get("Common_Ok"), IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
        var cancel = new Button { Content = T.Get("Common_Cancel"), IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
