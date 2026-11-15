using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Transfer;

public sealed record ImportResult(long RowsRead, long Inserted, long Updated, long Skipped, string? ErrorFile, bool CreatedTable)
{
    public long RowsWritten => Inserted + Updated;
}

/// <summary>The import stopped. Nothing was written (the transaction was rolled back).</summary>
public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Imports a file into a table inside one transaction: pre-SQL, table set-up for the load mode, rows
/// (one prepared statement), post-SQL. Uses a SAVEPOINT, so it also works inside a script job's transaction.
/// </summary>
public static class ImportEngine
{
    public const int TypeSampleRows = 10_000;
    private const string Savepoint = "sqlite_manager_import";

    public static Task<ImportResult> RunAsync(DatabaseSession session, ImportJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RunExclusiveAsync(runner => Run(runner, job, progress, cancellationToken), cancellationToken);
    }

    public static ImportResult Run(ISqlRunner runner, ImportJob job, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.TargetTable))
        {
            throw new ImportException("Choose the target table.");
        }

        using var source = SourceReaders.Open(job.SourcePath, job.SourceFormat, job.SourceOptions);
        var converter = new ImportValueConverter(job.SourceOptions);
        var table = SqlIdentifier.Quote(job.TargetTable);
        var tableExists = TableColumns(runner, job.TargetTable).Count > 0;
        var create = job.LoadMode == LoadMode.Replace || (!tableExists && job.CreateTableIfMissing);
        if (!tableExists && !create)
        {
            throw new ImportException($"The table \"{job.TargetTable}\" does not exist. Turn on \"create table if missing\" or choose another table.");
        }

        var mappings = ResolveMappings(job, source.Columns, create ? null : TableColumns(runner, job.TargetTable).Select(c => c.Name).ToList());

        // Rows are read once; when the table is created, the first rows decide the column types.
        using var rows = source.ReadRows().GetEnumerator();
        var sample = new List<SourceRow>();
        if (create)
        {
            while (sample.Count < TypeSampleRows && rows.MoveNext())
            {
                sample.Add(rows.Current);
            }
        }

        string? errorFile = null;
        ErrorFileWriter? errors = null;
        long read = 0, inserted = 0, updated = 0, skipped = 0;
        runner.Execute($"SAVEPOINT {Savepoint}");
        try
        {
            RunScript(runner, job.PreSql, "pre-SQL");

            if (create)
            {
                if (job.LoadMode == LoadMode.Replace)
                {
                    runner.Execute($"DROP TABLE IF EXISTS {table}");
                }

                var columns = mappings.Select(m =>
                {
                    var type = m.SourceIndex >= 0
                        ? converter.InferType(sample.Select(r => r.Values[m.SourceIndex]))
                        : converter.InferType([m.Default]);
                    return $"{SqlIdentifier.Quote(m.Target)} {type}";
                });
                runner.Execute($"CREATE TABLE {table} ({string.Join(", ", columns)})");
            }
            else if (job.LoadMode == LoadMode.Truncate)
            {
                runner.Execute($"DELETE FROM {table}");
            }

            var declared = TableColumns(runner, job.TargetTable).ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings)
            {
                mapping.DeclaredType = declared.GetValueOrDefault(mapping.Target) ?? string.Empty;
            }

            using var writer = new RowWriter(runner, job, mappings);
            foreach (var row in sample.Concat(Remaining(rows)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                read++;
                try
                {
                    if (row.Error is not null)
                    {
                        throw new FormatException(row.Error);
                    }

                    var values = new Dictionary<string, object?>(mappings.Count);
                    for (var i = 0; i < mappings.Count; i++)
                    {
                        var mapping = mappings[i];
                        var raw = mapping.SourceIndex >= 0 ? row.Values[mapping.SourceIndex] : mapping.Default;
                        try
                        {
                            values[$"@p{i}"] = converter.Convert(raw, mapping.DeclaredType);
                        }
                        catch (FormatException ex)
                        {
                            throw new FormatException($"Column \"{mapping.Target}\": {ex.Message}", ex);
                        }
                    }

                    if (writer.Write(values))
                    {
                        updated++;
                    }
                    else
                    {
                        inserted++;
                    }
                }
                catch (Exception ex) when (ex is FormatException or SqliteException)
                {
                    var message = ex is SqliteException sqlite ? Clean(sqlite) : ex.Message;
                    if (job.OnError == ErrorMode.Rollback)
                    {
                        throw new ImportException($"Row {row.Number}: {message} Nothing was imported.", ex);
                    }

                    errors ??= new ErrorFileWriter(errorFile = ErrorFilePath(job.SourcePath), source.Columns);
                    errors.Write(row, message);
                    skipped++;
                }

                if (read % 1000 == 0)
                {
                    progress?.Report(read);
                }
            }

            writer.Dispose();
            RunScript(runner, job.PostSql, "post-SQL");
            runner.Execute($"RELEASE {Savepoint}");
        }
        catch
        {
            runner.Execute($"ROLLBACK TO {Savepoint}");
            runner.Execute($"RELEASE {Savepoint}");
            errors?.Dispose();
            if (errorFile is not null)
            {
                File.Delete(errorFile); // Nothing was imported, so the list of skipped rows would mislead.
            }

            throw;
        }

        errors?.Dispose();
        progress?.Report(read);
        return new ImportResult(read, inserted, updated, skipped, errorFile, create);
    }

    private static IEnumerable<SourceRow> Remaining(IEnumerator<SourceRow> rows)
    {
        while (rows.MoveNext())
        {
            yield return rows.Current;
        }
    }

    private static void RunScript(ISqlRunner runner, string? sql, string what)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        foreach (var statement in SqlScriptSplitter.Split(sql))
        {
            try
            {
                runner.Execute(statement.Text);
            }
            catch (SqliteException ex)
            {
                throw new ImportException($"The {what} failed: {Clean(ex)} Nothing was imported.", ex);
            }
        }
    }

    private static List<(string Name, string Type, int PkOrder)> TableColumns(ISqlRunner runner, string table) =>
        runner.Query(
                "SELECT name, type, pk FROM pragma_table_xinfo(@t) WHERE hidden NOT IN (1, 2, 3) " +
                "AND EXISTS (SELECT 1 FROM pragma_table_list WHERE schema = 'main' AND name = @t AND type = 'table') ORDER BY cid",
                new Dictionary<string, object?> { ["@t"] = table })
            .Select(r => ((string)r[0]!, (string)r[1]!, Convert.ToInt32(r[2], CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>
    /// The columns to write. With no mappings, source columns are matched to target columns by name
    /// (all source columns when the table is created). Raises <see cref="ImportException"/> for unknown columns.
    /// </summary>
    private static List<Mapping> ResolveMappings(ImportJob job, IReadOnlyList<string> sourceColumns, List<string>? targetColumns)
    {
        int SourceIndex(string name) => sourceColumns.ToList().FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        var result = new List<Mapping>();
        if (job.ColumnMappings is not { Count: > 0 })
        {
            foreach (var (name, index) in sourceColumns.Select((c, i) => (c, i)))
            {
                var target = targetColumns is null ? name : targetColumns.FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    result.Add(new Mapping(target, index, null));
                }
            }

            if (result.Count == 0)
            {
                throw new ImportException($"No column in the file has the same name as a column of \"{job.TargetTable}\". Map the columns.");
            }

            return result;
        }

        foreach (var mapping in job.ColumnMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Target))
            {
                continue;
            }

            var index = -1;
            if (!string.IsNullOrWhiteSpace(mapping.Source))
            {
                index = SourceIndex(mapping.Source);
                if (index < 0)
                {
                    throw new ImportException($"The file has no column \"{mapping.Source}\". Its columns are: {string.Join(", ", sourceColumns)}.");
                }
            }
            else if (mapping.Default is null)
            {
                continue; // Neither a source nor a default: the column is left out.
            }

            if (targetColumns is not null && !targetColumns.Contains(mapping.Target, StringComparer.OrdinalIgnoreCase))
            {
                throw new ImportException($"The table \"{job.TargetTable}\" has no column \"{mapping.Target}\".");
            }

            if (result.Any(m => m.Target.Equals(mapping.Target, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ImportException($"Column \"{mapping.Target}\" is mapped more than once.");
            }

            result.Add(new Mapping(mapping.Target, index, mapping.Default));
        }

        if (result.Count == 0)
        {
            throw new ImportException("No columns are mapped.");
        }

        return result;
    }

    private static string ErrorFilePath(string sourcePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{name}.errors_{stamp}.csv");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name}.errors_{stamp}_{n}.csv");
        }

        return path;
    }

    private static string Clean(SqliteException ex)
    {
        var text = ex.Message;
        var start = text.IndexOf('\'');
        var end = text.LastIndexOf('\'');
        return (start >= 0 && end > start ? text[(start + 1)..end] : text).TrimEnd('.') + ".";
    }

    private sealed class Mapping(string target, int sourceIndex, string? @default)
    {
        public string Target { get; } = target;

        /// <summary>Index of the source column, or -1 when the value is <see cref="Default"/>.</summary>
        public int SourceIndex { get; } = sourceIndex;

        public string? Default { get; } = @default;

        public string DeclaredType { get; set; } = string.Empty;
    }

    /// <summary>Writes rows with the statement(s) for the load mode.</summary>
    private sealed class RowWriter : IDisposable
    {
        private readonly IPreparedStatement _insert;
        private readonly IPreparedStatement? _update;
        private readonly Dictionary<string, string> _updateParameters = [];

        public RowWriter(ISqlRunner runner, ImportJob job, List<Mapping> mappings)
        {
            var table = SqlIdentifier.Quote(job.TargetTable);
            var columns = string.Join(", ", mappings.Select(m => SqlIdentifier.Quote(m.Target)));
            var parameters = string.Join(", ", mappings.Select((_, i) => $"@p{i}"));
            var insert = $"INSERT INTO {table} ({columns}) VALUES ({parameters})";

            if (job.LoadMode != LoadMode.Upsert)
            {
                _insert = runner.Prepare(insert);
                return;
            }

            var keys = job.KeyColumns ?? [];
            if (keys.Count == 0)
            {
                throw new ImportException("Upsert needs key columns.");
            }

            var keyIndexes = keys.Select(k => mappings.FindIndex(m => m.Target.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (keyIndexes.Contains(-1))
            {
                throw new ImportException($"Every key column must be mapped. Key columns: {string.Join(", ", keys)}.");
            }

            var nonKeys = Enumerable.Range(0, mappings.Count).Where(i => !keyIndexes.Contains(i)).ToList();
            if (HasUniqueIndex(runner, job.TargetTable, keys))
            {
                var conflict = string.Join(", ", keys.Select(SqlIdentifier.Quote));
                var action = nonKeys.Count == 0
                    ? "DO NOTHING"
                    : "DO UPDATE SET " + string.Join(", ", nonKeys.Select(i => $"{SqlIdentifier.Quote(mappings[i].Target)} = excluded.{SqlIdentifier.Quote(mappings[i].Target)}"));
                _insert = runner.Prepare($"{insert} ON CONFLICT ({conflict}) {action}");
                return;
            }

            // No unique index on the keys: update matching rows first, then insert when nothing matched.
            _insert = runner.Prepare(insert);
            if (nonKeys.Count > 0)
            {
                var sets = string.Join(", ", nonKeys.Select(i => $"{SqlIdentifier.Quote(mappings[i].Target)} = @p{i}"));
                var where = string.Join(" AND ", keyIndexes.Select(i => $"{SqlIdentifier.Quote(mappings[i].Target)} IS @p{i}"));
                _update = runner.Prepare($"UPDATE {table} SET {sets} WHERE {where}");
            }
            else
            {
                var where = string.Join(" AND ", keyIndexes.Select(i => $"{SqlIdentifier.Quote(mappings[i].Target)} IS @p{i}"));
                _update = runner.Prepare($"UPDATE {table} SET {SqlIdentifier.Quote(mappings[keyIndexes[0]].Target)} = {SqlIdentifier.Quote(mappings[keyIndexes[0]].Target)} WHERE {where}");
            }
        }

        /// <summary>
        /// Writes one row. Returns true if an existing row was updated (UPDATE-then-INSERT upsert), false otherwise.
        /// INSERT … ON CONFLICT DO UPDATE reports one change either way, so those rows count as written (inserted).
        /// </summary>
        public bool Write(Dictionary<string, object?> values)
        {
            if (_update is not null && _update.Execute(values) > 0)
            {
                return true;
            }

            _insert.Execute(values);
            return false;
        }

        public void Dispose()
        {
            _insert.Dispose();
            _update?.Dispose();
        }

        /// <summary>True if the primary key or a full (not partial) unique index covers exactly the key columns.</summary>
        private static bool HasUniqueIndex(ISqlRunner runner, string table, List<string> keys)
        {
            var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            var p = new Dictionary<string, object?> { ["@t"] = table };
            var pk = runner.Query("SELECT name FROM pragma_table_info(@t) WHERE pk > 0", p).Select(r => (string)r[0]!).ToList();
            if (pk.Count > 0 && keySet.SetEquals(pk))
            {
                return true;
            }

            var indexes = runner.Query(
                "SELECT il.name, ii.name FROM pragma_index_list(@t) AS il JOIN pragma_index_info(il.name) AS ii WHERE il.\"unique\" = 1 AND il.partial = 0", p);
            return indexes.GroupBy(r => (string)r[0]!).Any(g => g.All(r => r[1] is not null) && keySet.SetEquals(g.Select(r => (string)r[1]!)));
        }
    }

    /// <summary>Writes skipped rows to a CSV file with the reason. Created only when a row is skipped.</summary>
    private sealed class ErrorFileWriter : IDisposable
    {
        private readonly StreamWriter _stream;
        private readonly CsvWriter _csv;

        public ErrorFileWriter(string path, IReadOnlyList<string> columns)
        {
            _stream = new StreamWriter(path, append: false, new UTF8Encoding(true));
            _csv = new CsvWriter(_stream, CultureInfo.InvariantCulture);
            _csv.WriteField("Row");
            _csv.WriteField("Error");
            foreach (var column in columns)
            {
                _csv.WriteField(column);
            }

            _csv.NextRecord();
        }

        public void Write(SourceRow row, string message)
        {
            _csv.WriteField(row.Number);
            _csv.WriteField(message);
            foreach (var value in row.Values)
            {
                _csv.WriteField(value switch
                {
                    null => string.Empty,
                    double d => d.ToString("R", CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString(),
                });
            }

            _csv.NextRecord();
        }

        public void Dispose()
        {
            _csv.Dispose();
            _stream.Dispose();
        }
    }
}
