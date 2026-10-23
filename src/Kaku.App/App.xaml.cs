using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Kaku.App.Localization;

namespace Kaku.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = T.Es;
        CultureInfo.DefaultThreadCurrentUICulture = T.Es;
        CultureInfo.CurrentCulture = T.Es;
        CultureInfo.CurrentUICulture = T.Es;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(T.Es.IetfLanguageTag)));

        DispatcherUnhandledException += OnUnhandledException;
        Services.Theme.WatchSystem();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.FirstOrDefault(a => a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) is { } path)
            _ = window.OpenBookAsync(System.IO.Path.GetFullPath(path));
        else
            window.Dispatcher.BeginInvoke(async () => await window.CheckRecoveryOnStartupAsync(), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Appends an exception to %APPDATA%\Kaku\error.log.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.AppSettings.Folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Services.AppSettings.Folder, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch (System.IO.IOException)
        {
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(MainWindow!, T.F("Error_Unexpected", e.Exception.Message), "Kaku", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
