using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Kaku.App.Localization;

/// <summary>
/// UI strings from Strings.resx. In XAML: <c>Header="{l:T Menu_File}"</c>; in code: <c>T.Get("Menu_File")</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    private static readonly ResourceManager Resources = new("Kaku.App.Localization.Strings", typeof(T).Assembly);

    /// <summary>Culture used for numbers shown in the UI (45.210 · 45,8 %).</summary>
    public static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public T(string key) => Key = key;

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Get(Key);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string F(string key, params object?[] args) => string.Format(Es, Get(key), args);

    public static string N0(long n) => n.ToString("N0", Es);

    public static string N1(double n) => n.ToString("N1", Es);
}
