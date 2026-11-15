using System.Text;
using System.Text.Json;
using SqliteManager.Core.IO;

namespace SqliteManager.Core.Settings;

/// <summary>User settings stored in settings.json.</summary>
public sealed class AppSettings
{
    public const int DefaultQueryRowLimit = 100_000;
    public const int MinQueryRowLimit = 1;
    public const int MaxQueryRowLimit = 10_000_000;

    public const int MaxRecentFiles = 10;
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    /// <summary>Most rows kept per query result set.</summary>
    public int QueryRowLimit { get; set; } = DefaultQueryRowLimit;

    /// <summary>Recently opened databases, newest first.</summary>
    public List<RecentFile> RecentFiles { get; set; } = [];

    /// <summary>Main window position and pane sizes, or null before the first run.</summary>
    public WindowLayout? Window { get; set; }

    /// <summary><see cref="LightTheme"/> or <see cref="DarkTheme"/>.</summary>
    public string Theme { get; set; } = LightTheme;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDarkTheme => Theme == DarkTheme;

    /// <summary>Moves (or adds) a file to the top of the recent list.</summary>
    public void AddRecentFile(string path, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RecentFiles.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, new RecentFile { Path = path, ReadOnly = readOnly });
        Normalize();
    }

    public void RemoveRecentFile(string path) =>
        RecentFiles.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts every value in its allowed range.</summary>
    public void Normalize()
    {
        QueryRowLimit = Math.Clamp(QueryRowLimit, MinQueryRowLimit, MaxQueryRowLimit);
        RecentFiles = (RecentFiles ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Path))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentFiles)
            .ToList();
        Theme = string.Equals(Theme, DarkTheme, StringComparison.OrdinalIgnoreCase) ? DarkTheme : LightTheme;
        Window?.Normalize();
    }
}

public sealed class RecentFile
{
    public string Path { get; set; } = string.Empty;

    public bool ReadOnly { get; set; }
}

/// <summary>Saved main window layout (in 96-dpi pixels for pane sizes; screen pixels for the window bounds).</summary>
public sealed class WindowLayout
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }

    public int ExplorerWidth { get; set; } = 280;

    public int OutputHeight { get; set; } = 200;

    public bool ExplorerVisible { get; set; } = true;

    public bool OutputVisible { get; set; } = true;

    public void Normalize()
    {
        Width = Math.Max(Width, 400);
        Height = Math.Max(Height, 300);
        ExplorerWidth = Math.Clamp(ExplorerWidth, 100, 2000);
        OutputHeight = Math.Clamp(OutputHeight, 60, 2000);
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>Loads settings. A missing or damaged file gives the defaults.</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        try
        {
            await using var stream = File.OpenRead(FilePath);
            settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return AtomicFile.WriteAllTextAsync(FilePath, json, new UTF8Encoding(false), cancellationToken);
    }
}
