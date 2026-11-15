using System.Text;
using System.Text.Json;
using SqliteManager.Core.IO;

namespace SqliteManager.Core.Jobs;

/// <summary>Reads and writes jobs.json. Writes are atomic (temp file, then replace).</summary>
public sealed class JobStore
{
    public JobStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "jobs.json");

    public string FilePath { get; }

    /// <summary>Loads the jobs. A missing file gives an empty list. A damaged file raises <see cref="JsonException"/>.</summary>
    public async Task<JobFile> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new JobFile();
        }

        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<JobFile>(stream, JobJson.Options, cancellationToken).ConfigureAwait(false) ?? new JobFile();
    }

    public Task SaveAsync(JobFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var duplicate = file.Jobs.GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one job is named \"{duplicate.Key}\".");
        }

        var json = JsonSerializer.Serialize(file, JobJson.Options);
        return AtomicFile.WriteAllTextAsync(FilePath, json, new UTF8Encoding(false), cancellationToken);
    }
}

/// <summary>One run of a job (or of a step inside a script job).</summary>
public sealed record RunRecord(
    string Job,
    DateTime Start,
    DateTime End,
    long Rows,
    string Status,
    string? Error,
    string? Parent = null)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Appends one JSON line per run to history.jsonl. Safe to use from the app and the CLI at the same time.</summary>
public sealed class RunHistoryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public RunHistoryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "history.jsonl");

    public string FilePath { get; }

    public void Append(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = JsonSerializer.Serialize(record, Options) + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

        // Another process may be writing at the same moment; retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Utf8.GetBytes(line);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>All records, newest first. Lines that cannot be read are skipped.</summary>
    public async Task<IReadOnlyList<RunRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        string text;
        await using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Utf8))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var records = new List<RunRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunRecord>(line, Options) is { Job: not null } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        records.Reverse();
        return records;
    }
}
