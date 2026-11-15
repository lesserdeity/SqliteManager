using SqliteManager.App.Ui;
using SqliteManager.Core.IO;
using SqliteManager.Core.Settings;

namespace SqliteManager.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // Settings are read before the window exists: the theme and the window layout are applied at startup.
        var store = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings;
        string? settingsError = null;
        try
        {
            settings = store.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
            settingsError = ex.Message;
        }

        Theme.Initialize(settings.IsDarkTheme);
        if (settings.IsDarkTheme)
        {
#pragma warning disable WFO5001 // Dark mode is marked experimental in WinForms.
            Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        }

        Application.Run(new MainForm(store, settings, settingsError));
    }
}
