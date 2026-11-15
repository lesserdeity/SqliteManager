namespace SqliteManager.Core.Logging;

/// <summary>One SQL statement that the application issued.</summary>
public sealed record SqlLogEntry(
    DateTime Timestamp,
    string Database,
    string Sql,
    IReadOnlyList<KeyValuePair<string, object?>> Parameters,
    TimeSpan Duration,
    string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Receives every SQL statement the application issues.</summary>
public interface ISqlLog
{
    /// <summary>Raised after an entry is added. May be raised on any thread.</summary>
    event EventHandler<SqlLogEntry>? EntryAdded;

    /// <summary>Raised after the log is cleared. May be raised on any thread.</summary>
    event EventHandler? Cleared;

    void Add(SqlLogEntry entry);

    IReadOnlyList<SqlLogEntry> GetEntries();

    void Clear();
}

/// <summary>Thread-safe in-memory SQL log that keeps the most recent entries.</summary>
public sealed class SqlLog : ISqlLog
{
    public const int DefaultCapacity = 5000;

    private readonly Queue<SqlLogEntry> _entries = new();
    private readonly Lock _sync = new();

    public SqlLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public event EventHandler<SqlLogEntry>? EntryAdded;

    public event EventHandler? Cleared;

    public void Add(SqlLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<SqlLogEntry> GetEntries()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
