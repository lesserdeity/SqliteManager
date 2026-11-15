using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Data;

/// <summary>A database attached to a session's connection (ATTACH DATABASE).</summary>
public sealed record AttachedDatabase(string Name, string FilePath);

/// <summary>Attaches and detaches database files so queries can use <c>name.table</c> across files.</summary>
public sealed class AttachService
{
    private readonly DatabaseSession _session;

    public AttachService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Attached databases (not main or temp), in attach order.</summary>
    public async Task<IReadOnlyList<AttachedDatabase>> ListAsync(CancellationToken cancellationToken = default) =>
        await _session.QueryAsync(
            "SELECT name, file FROM pragma_database_list WHERE name NOT IN ('main', 'temp') ORDER BY seq",
            r => new AttachedDatabase(r.GetString(0), r.IsDBNull(1) ? string.Empty : r.GetString(1)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>A schema name made from the file name: letters, digits, and underscores, not used yet.</summary>
    public static string SuggestName(string filePath, IEnumerable<string> usedNames)
    {
        var used = new HashSet<string>(usedNames, StringComparer.OrdinalIgnoreCase) { "main", "temp" };
        var stem = new string(Path.GetFileNameWithoutExtension(filePath).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (stem.Length == 0 || char.IsAsciiDigit(stem[0]))
        {
            stem = "db" + stem;
        }

        var name = stem;
        for (var i = 2; used.Contains(name); i++)
        {
            name = $"{stem}{i}";
        }

        return name;
    }

    /// <summary>Attaches an existing file. The file must exist (ATTACH would otherwise create an empty one).</summary>
    public async Task AttachAsync(string filePath, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The file '{fullPath}' does not exist.", fullPath);
        }

        name = name.Trim();
        if (name.Equals("main", StringComparison.OrdinalIgnoreCase) || name.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The names main and temp are reserved.", nameof(name));
        }

        var attached = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (attached.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A database is already attached as {name}.", nameof(name));
        }

        if (attached.Any(a => string.Equals(a.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) || string.Equals(fullPath, _session.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("That file is already open on this connection.", nameof(filePath));
        }

        await _session.ExecuteNonQueryAsync(
            $"ATTACH DATABASE @path AS {SqlIdentifier.Quote(name)}",
            new Dictionary<string, object?> { ["@path"] = fullPath },
            cancellationToken).ConfigureAwait(false);
    }

    public Task DetachAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _session.ExecuteNonQueryAsync($"DETACH DATABASE {SqlIdentifier.Quote(name)}", cancellationToken: cancellationToken);
    }
}
