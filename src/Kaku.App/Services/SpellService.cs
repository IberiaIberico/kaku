using System.IO;
using Kaku.Core.Checks;

namespace Kaku.App.Services;

/// <summary>
/// The Spanish spell checker, loaded once in the background from the embedded LibreOffice dictionary
/// (es_ES, GPL/LGPL/MPL), plus the personal dictionary in %APPDATA%\Kaku\diccionario-personal.txt.
/// </summary>
public static class SpellService
{
    private static readonly Lazy<Task<SpellChecker?>> Loader = new(() => Task.Run(Load));

    public static string UserFile { get; } = Path.Combine(AppSettings.Folder, "diccionario-personal.txt");

    /// <summary>The checker, or null while it loads (or if it failed).</summary>
    public static SpellChecker? Checker => Loader.Value.IsCompletedSuccessfully ? Loader.Value.Result : null;

    public static Task<SpellChecker?> GetAsync() => Loader.Value;

    /// <summary>Raised on the thread pool when the dictionary is ready.</summary>
    public static event Action? Ready;

    private static SpellChecker? Load()
    {
        try
        {
            var asm = typeof(SpellService).Assembly;
            using var dic = asm.GetManifestResourceStream("Kaku.es_ES.dic")!;
            using var aff = asm.GetManifestResourceStream("Kaku.es_ES.aff")!;
            var checker = new SpellChecker(dic, aff);
            checker.AddUserWords(SpellChecker.ReadUserFile(UserFile));
            Ready?.Invoke();
            return checker;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }

    /// <summary>Raised (on the UI thread) when words are added or ignored: views redraw.</summary>
    public static event Action? WordsChanged;

    public static void Ignore(string word)
    {
        Checker?.Ignore(word);
        WordsChanged?.Invoke();
    }

    public static void AddToDictionary(string word)
    {
        if (Checker is not { } c) return;
        c.AddUserWord(word);
        WordsChanged?.Invoke();
        try
        {
            c.WriteUserFile(UserFile);
        }
        catch (IOException ex)
        {
            App.LogError(ex);
        }
    }
}
