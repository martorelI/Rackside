using Rackside.App.Ui;
using Rackside.Core.Localization;
using Rackside.Core.Settings;

namespace Rackside.App;

internal static class Program
{
    private static string BaseFolder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Report(e.Exception);
            // Security rule 9: everything next to the program (or under DataFolder), never a fixed path.
            var settingsPath = AppSettings.PathFor(BaseFolder);
            var settings = AppSettings.Read(settingsPath);
            var t = Translator.For(settings.Language);
            Text.Initialize(t);
            var app = new AppState(t, settings, settingsPath, DataPaths.From(BaseFolder, settings));
            using var form = new MainForm(app);
            Application.Run(form);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Crash(ex);
        }
    }

    /// <summary>An error on the window thread: logged and shown; the application keeps running.</summary>
    private static void Report(Exception ex)
    {
        var file = WriteError(ex);
        Dialogs.Error(null, ex.Message + (file is null ? string.Empty : "\r\n\r\n" + file));
    }

    /// <summary>An error that ends the application: never silently. The full detail goes to rackside-error.log.</summary>
    private static void Crash(Exception? ex)
    {
        var file = WriteError(ex);
        try
        {
            MessageBox.Show((ex?.Message ?? "?") + "\r\n\r\n" + (file ?? string.Empty), "Rackside", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidOperationException)
        {
        }
        Environment.Exit(1);
    }

    /// <summary>Appends the exception (with its stack) to rackside-error.log next to the program, or in TEMP if it cannot write there.</summary>
    private static string? WriteError(Exception? ex)
    {
        var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " " + Environment.OSVersion + " .NET " + Environment.Version + "\r\n" + ex + "\r\n\r\n";
        foreach (var dir in new[] { BaseFolder, Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(dir, "rackside-error.log");
                File.AppendAllText(path, text);
                return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}

/// <summary>What every page shares: language, settings and the data folders.</summary>
internal sealed record AppState(Translator T, AppSettings Settings, string SettingsPath, DataPaths Paths);
