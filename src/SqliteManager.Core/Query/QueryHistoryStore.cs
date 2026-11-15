using System.Text;
using System.Text.Json;
using SqliteManager.Core.IO;

namespace SqliteManager.Core.Query;

/// <summary>One run of a query, as stored in the history file.</summary>
public sealed record QueryHistoryEntry(
    DateTime Timestamp,
    string DatabasePath,
    string Sql,
    long DurationMs,
    bool Succeeded,
    long Rows,
    string? Error);

/// <summary>Stores query history as one JSON object per line and keeps only the most recent entries.</summary>
public sealed class QueryHistoryStore
{
    public const int DefaultMaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public QueryHistoryStore(string filePath, int maxEntries = DefaultMaxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        FilePath = filePath;
        MaxEntries = maxEntries;
    }

    public string FilePath { get; }

    public int MaxEntries { get; }

    /// <summary>Returns all entries, newest first. Lines that cannot be read are skipped.</summary>
    public async Task<IReadOnlyList<QueryHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            entries.Reverse();
            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds an entry. When the file holds more than <see cref="MaxEntries"/>, the oldest are removed.</summary>
    public async Task AppendAsync(QueryHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            entries.Add(entry);
            if (entries.Count > MaxEntries)
            {
                var kept = entries.Skip(entries.Count - MaxEntries);
                var text = string.Concat(kept.Select(e => JsonSerializer.Serialize(e, JsonOptions) + "\n"));
                await AtomicFile.WriteAllTextAsync(FilePath, text, Utf8, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                await File.AppendAllTextAsync(FilePath, JsonSerializer.Serialize(entry, JsonOptions) + "\n", Utf8, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Entries whose SQL or database path contains <paramref name="text"/> (ignoring case).</summary>
    public static IReadOnlyList<QueryHistoryEntry> Search(IEnumerable<QueryHistoryEntry> entries, string? text)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(text))
        {
            return entries.ToList();
        }

        var term = text.Trim();
        return entries.Where(e =>
                e.Sql.Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.DatabasePath.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<List<QueryHistoryEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var entries = new List<QueryHistoryEntry>();
        if (!File.Exists(FilePath))
        {
            return entries;
        }

        var lines = await File.ReadAllLinesAsync(FilePath, Utf8, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<QueryHistoryEntry>(line, JsonOptions) is { Sql: not null, DatabasePath: not null } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Skip damaged lines.
            }
        }

        return entries;
    }
}
