using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Sesame.Services;
using Sesame.Services.GameOptimizer;
using Sesame.Services.Mii;

namespace Sesame;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
        AppUpdate.BeforeRestart = FflRenderer.ShutdownAll;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FflRenderer.ShutdownAll();
        HostEnvironment.ApplyArgs(e.Args);

        try
        {
            AppDataPaths.EnsureProtected();
            base.OnStartup(e);
            ThemeManager.LoadSaved();
            TranslateSettings.Load();
            OptimizerSettings.Load();
            LaunchConfigStore.Load();
            LibraryPaths.Load();
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowStartupError(ex);
            Shutdown(-1);
        }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(e.Exception.Message, AppBrand.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log(ex);
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log(e.Exception);
        e.SetObserved();
    }

    private static void ShowStartupError(Exception ex)
    {
        Log(ex);
        MessageBox.Show(
            AppBrand.ShortName + " kon niet starten.\n\n" + ex.Message + "\n\nDetails: %APPDATA%\\SESAME\\crash.log",
            AppBrand.ShortName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void Log(Exception ex)
    {
        try
        {
            AppDataPaths.EnsureProtected();
            var path = AppDataPaths.Combine("crash.log");
            File.WriteAllText(path, DateTime.Now + Environment.NewLine + Redact(ex.ToString()));
            AppDataPaths.RestrictFile(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string Redact(string text)
    {
        text = Regex.Replace(text,
            @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----",
            "[redacted-key]");
        text = Regex.Replace(text,
            @"(Bearer|DeepL-Auth-Key|Authorization:)\s+\S+",
            "$1 [redacted]",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text,
            @"(password|passphrase|api[_-]?key|deeplkey|steamgriddbkey)\s*[=:]\s*\S+",
            "$1=[redacted]",
            RegexOptions.IgnoreCase);
        return text;
    }
}
