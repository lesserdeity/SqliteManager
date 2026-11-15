using SqliteManager.Core.Data;

namespace SqliteManager.Core.Maintenance;

/// <summary>Result of a check: OK, or the problem rows SQLite reported.</summary>
public sealed record CheckResult(bool IsOk, RowSet Rows, TimeSpan Duration);

/// <summary>Result of VACUUM: the file size before and after.</summary>
public sealed record VacuumResult(long SizeBefore, long SizeAfter, TimeSpan Duration);

/// <summary>VACUUM, ANALYZE, integrity_check, and foreign_key_check on an open database.</summary>
public sealed class MaintenanceService
{
    private readonly DatabaseSession _session;

    public MaintenanceService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Rebuilds the file to remove free pages. Fails inside an open transaction.</summary>
    public async Task<VacuumResult> VacuumAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable("VACUUM");
        var before = FileSize();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _session.RunExclusiveAsync(runner =>
        {
            if (runner.IsInTransaction)
            {
                throw new InvalidOperationException("VACUUM cannot run while a transaction is open. Commit or roll back first (for example in a query tab).");
            }

            return runner.Execute("VACUUM", null, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        return new VacuumResult(before, FileSize(), watch.Elapsed);
    }

    /// <summary>Gathers statistics for the query planner.</summary>
    public async Task<TimeSpan> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable("ANALYZE");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _session.ExecuteNonQueryAsync("ANALYZE", cancellationToken: cancellationToken).ConfigureAwait(false);
        return watch.Elapsed;
    }

    /// <summary>
    /// Runs <c>PRAGMA integrity_check</c> (or the faster <c>quick_check</c>). OK when SQLite returns the single row "ok".
    /// </summary>
    public async Task<CheckResult> IntegrityCheckAsync(bool quick = false, int maxErrors = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxErrors);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pragma = quick ? "quick_check" : "integrity_check";
        var rows = await _session.QueryRowsAsync($"PRAGMA {pragma}({maxErrors})", cancellationToken: cancellationToken).ConfigureAwait(false);
        var ok = rows.Rows.Count == 1 && rows.Rows[0][0] is string s && s == "ok";
        return new CheckResult(ok, rows, watch.Elapsed);
    }

    /// <summary>Runs <c>PRAGMA foreign_key_check</c>. Each row is a row that breaks a foreign key.</summary>
    public async Task<CheckResult> ForeignKeyCheckAsync(CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = await _session.QueryRowsAsync(
            "SELECT \"table\", rowid, parent, fkid FROM pragma_foreign_key_check",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new CheckResult(rows.Rows.Count == 0, rows, watch.Elapsed);
    }

    private long FileSize()
    {
        try
        {
            return new FileInfo(_session.FilePath).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private void EnsureWritable(string operation)
    {
        if (_session.IsReadOnly)
        {
            throw new InvalidOperationException($"{operation} needs write access. The database is open read-only.");
        }
    }
}
