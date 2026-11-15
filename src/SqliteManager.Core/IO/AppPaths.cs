namespace SqliteManager.Core.IO;

/// <summary>Locations of the application's own files under %APPDATA%\SqliteManager.</summary>
public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqliteManager");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string QueryHistoryFile => Path.Combine(DataFolder, "query-history.jsonl");
}
