using System.Globalization;
using SqliteManager.Core.Data;

namespace SqliteManager.Core.Maintenance;

/// <summary>The PRAGMA settings shown in the PRAGMA editor.</summary>
public sealed record PragmaValues(
    string JournalMode,
    bool ForeignKeys,
    int Synchronous,
    long CacheSize,
    int PageSize,
    int UserVersion,
    long PageCount,
    long FreelistCount,
    string Encoding);

/// <summary>Reads and changes PRAGMA settings. Values are checked strictly, because PRAGMA values cannot be parameters.</summary>
public sealed class PragmaService
{
    public static readonly IReadOnlyList<string> JournalModes = ["DELETE", "TRUNCATE", "PERSIST", "MEMORY", "WAL", "OFF"];

    /// <summary>synchronous values by number (0 to 3).</summary>
    public static readonly IReadOnlyList<string> SynchronousModes = ["OFF", "NORMAL", "FULL", "EXTRA"];

    public static readonly IReadOnlyList<int> PageSizes = [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    private readonly DatabaseSession _session;

    public PragmaService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public async Task<PragmaValues> ReadAsync(CancellationToken cancellationToken = default)
    {
        var row = (await _session.QueryRowsAsync(
            "SELECT (SELECT journal_mode FROM pragma_journal_mode), (SELECT foreign_keys FROM pragma_foreign_keys), " +
            "(SELECT synchronous FROM pragma_synchronous), (SELECT cache_size FROM pragma_cache_size), " +
            "(SELECT page_size FROM pragma_page_size), (SELECT user_version FROM pragma_user_version), " +
            "(SELECT page_count FROM pragma_page_count), (SELECT freelist_count FROM pragma_freelist_count), " +
            "(SELECT encoding FROM pragma_encoding)",
            cancellationToken: cancellationToken).ConfigureAwait(false)).Rows[0];

        return new PragmaValues(
            Convert.ToString(row[0], CultureInfo.InvariantCulture)!.ToUpperInvariant(),
            Convert.ToInt64(row[1], CultureInfo.InvariantCulture) != 0,
            (int)Convert.ToInt64(row[2], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[3], CultureInfo.InvariantCulture),
            (int)Convert.ToInt64(row[4], CultureInfo.InvariantCulture),
            (int)Convert.ToInt64(row[5], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[6], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[7], CultureInfo.InvariantCulture),
            Convert.ToString(row[8], CultureInfo.InvariantCulture)!);
    }

    /// <summary>
    /// The statements that change <paramref name="current"/> into <paramref name="wanted"/>, in a safe order.
    /// Raises <see cref="ArgumentException"/> for a value SQLite does not accept.
    /// </summary>
    public static IReadOnlyList<string> PlanChanges(PragmaValues current, PragmaValues wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);
        Validate(wanted);

        var statements = new List<string>();
        if (wanted.ForeignKeys != current.ForeignKeys)
        {
            statements.Add($"PRAGMA foreign_keys = {(wanted.ForeignKeys ? "ON" : "OFF")}");
        }

        if (wanted.Synchronous != current.Synchronous)
        {
            statements.Add($"PRAGMA synchronous = {SynchronousModes[wanted.Synchronous]}");
        }

        if (wanted.CacheSize != current.CacheSize)
        {
            statements.Add($"PRAGMA cache_size = {wanted.CacheSize.ToString(CultureInfo.InvariantCulture)}");
        }

        if (wanted.UserVersion != current.UserVersion)
        {
            statements.Add($"PRAGMA user_version = {wanted.UserVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        var journalChanges = !string.Equals(wanted.JournalMode, current.JournalMode, StringComparison.OrdinalIgnoreCase);
        var setJournal = $"PRAGMA journal_mode = {wanted.JournalMode.ToUpperInvariant()}";
        if (wanted.PageSize != current.PageSize)
        {
            // A new page size takes effect only through VACUUM, and never in WAL mode.
            if (wanted.JournalMode.Equals("WAL", StringComparison.OrdinalIgnoreCase) && current.JournalMode.Equals("WAL", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("page_size cannot change while journal_mode is WAL. Choose another journal mode first.", nameof(wanted));
            }

            if (journalChanges && current.JournalMode.Equals("WAL", StringComparison.OrdinalIgnoreCase))
            {
                statements.Add(setJournal);
                journalChanges = false;
            }

            statements.Add($"PRAGMA page_size = {wanted.PageSize.ToString(CultureInfo.InvariantCulture)}");
            statements.Add("VACUUM");
        }

        if (journalChanges)
        {
            statements.Add(setJournal);
        }

        return statements;
    }

    /// <summary>
    /// Applies the changes and returns the values read back afterwards. Settings that SQLite refuses silently
    /// (for example foreign_keys inside a transaction) show up as unchanged in the result.
    /// </summary>
    public async Task<PragmaValues> ApplyAsync(PragmaValues wanted, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var statements = PlanChanges(current, wanted);
        if (_session.IsReadOnly && statements.Any(s => !s.StartsWith("PRAGMA cache_size", StringComparison.Ordinal) && !s.StartsWith("PRAGMA foreign_keys", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The database is open read-only. Only cache_size and foreign_keys can be changed.");
        }

        await _session.RunExclusiveAsync(runner =>
        {
            if (runner.IsInTransaction && statements.Any(s => s is "VACUUM" || s.StartsWith("PRAGMA journal_mode", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("journal_mode and page_size cannot change while a transaction is open. Commit or roll back first.");
            }

            foreach (var statement in statements)
            {
                // Some PRAGMAs return a row; Query reads it so the change completes.
                runner.Query(statement);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }
    public static void Validate(PragmaValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!JournalModes.Contains(values.JournalMode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"journal_mode must be one of {string.Join(", ", JournalModes)}.", nameof(values));
        }

        if (values.Synchronous is < 0 or > 3)
        {
            throw new ArgumentException("synchronous must be 0 (OFF), 1 (NORMAL), 2 (FULL), or 3 (EXTRA).", nameof(values));
        }

        if (!PageSizes.Contains(values.PageSize))
        {
            throw new ArgumentException("page_size must be a power of two from 512 to 65536.", nameof(values));
        }
    }
}
