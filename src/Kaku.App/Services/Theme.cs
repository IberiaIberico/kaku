using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kaku.App.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ThemeChoice>))]
public enum ThemeChoice
{
    Light,
    Dark,
    System,
}

/// <summary>
/// Light/dark theme. Kaku's own colors are brushes in Themes/Light.xaml and Themes/Dark.xaml (use them with
/// DynamicResource, or <see cref="Brush"/> in code); in dark mode the standard controls use WPF's Fluent theme.
/// </summary>
public static class Theme
{
    private static ResourceDictionary? _palette;
    private static ThemeChoice _choice;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the palette changed (things that do not use DynamicResource repaint here).</summary>
    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        bool dark = choice == ThemeChoice.Dark || (choice == ThemeChoice.System && SystemPrefersDark());
        if (_palette != null && dark == IsDark) return;
        IsDark = dark;

        var app = Application.Current;
#pragma warning disable WPF0001 // ThemeMode is marked experimental
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.None;
#pragma warning restore WPF0001
        if (_palette != null) app.Resources.MergedDictionaries.Remove(_palette);
        var palette = Load(dark ? "Dark" : "Light");
        var controls = Load("Controls");
        foreach (var style in controls.Values.OfType<Style>())
            style.BasedOn = app.TryFindResource(style.TargetType) as Style;
        palette.MergedDictionaries.Add(controls);
        app.Resources.MergedDictionaries.Add(palette);
        _palette = palette;
        // Fluent turns the title bars dark but leaves them so when the theme goes back to None
        if (!dark)
            foreach (Window w in app.Windows)
                SetDarkTitleBar(w, false);
        Changed?.Invoke();
    }

    /// <summary>Follows Windows when the choice is "System".</summary>
    public static void WatchSystem() => SystemEvents.UserPreferenceChanged += (_, e) =>
    {
        if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
            Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
    };

    private static ResourceDictionary Load(string name) => new() { Source = new Uri($"pack://application:,,,/Kaku;component/Themes/{name}.xaml") };

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Color Color(string key) => ((SolidColorBrush)Brush(key)).Color;

    private static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
