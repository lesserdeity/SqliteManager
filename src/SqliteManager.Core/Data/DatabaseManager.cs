using Microsoft.Data.Sqlite;
using SqliteManager.Core.Logging;

namespace SqliteManager.Core.Data;

/// <summary>Creates, opens, tracks, and closes database sessions.</summary>
public sealed class DatabaseManager : IAsyncDisposable
{
    private readonly List<DatabaseSession> _sessions = [];
    private readonly Lock _sync = new();

    public DatabaseManager(ISqlLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Log = log;
    }

    public ISqlLog Log { get; }

    public IReadOnlyList<DatabaseSession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>Creates a new, empty database file. Fails if the file already exists.</summary>
    public async Task<DatabaseSession> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        if (File.Exists(fullPath))
        {
            throw new IOException($"The file '{fullPath}' already exists.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null && !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
        }

        EnsureNotOpen(fullPath);
        var session = await DatabaseSession.OpenAsync(fullPath, SqliteOpenMode.ReadWriteCreate, Log, cancellationToken)
            .ConfigureAwait(false);
        Track(session);
        return session;
    }

    /// <summary>Opens an existing database file.</summary>
    public async Task<DatabaseSession> OpenAsync(string path, bool readOnly = false, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The file '{fullPath}' does not exist.", fullPath);
        }

        EnsureNotOpen(fullPath);
        var mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite;
        var session = await DatabaseSession.OpenAsync(fullPath, mode, Log, cancellationToken).ConfigureAwait(false);
        Track(session);
        return session;
    }

    public bool IsOpen(string path)
    {
        var fullPath = NormalizePath(path);
        lock (_sync)
        {
            return _sessions.Any(s => string.Equals(s.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task CloseAsync(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            _sessions.Remove(session);
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions)
        {
            await CloseAsync(session).ConfigureAwait(false);
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private void EnsureNotOpen(string fullPath)
    {
        if (IsOpen(fullPath))
        {
            throw new InvalidOperationException($"The database '{fullPath}' is already open.");
        }
    }

    private void Track(DatabaseSession session)
    {
        lock (_sync)
        {
            _sessions.Add(session);
        }
    }
}
