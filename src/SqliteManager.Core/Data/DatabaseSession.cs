using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Data;

/// <summary>Result of one statement: rows (with a flag if the row limit cut them off) or rows affected (-1 if not DML).</summary>
public sealed record StatementOutcome(RowSet? Rows, bool Truncated, int RowsAffected);

/// <summary>Runs statements on a connection that the caller holds exclusively. Every statement is logged.</summary>
public interface ISqlRunner
{
    /// <summary>True if a transaction is open on the connection.</summary>
    bool IsInTransaction { get; }

    int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters = null);

    /// <summary>Runs a statement that is interrupted (raising <see cref="OperationCanceledException"/>) when <paramref name="cancellationToken"/> fires.</summary>
    int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    IReadOnlyList<object?[]> Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null);

    /// <summary>Streams the rows of a query: <paramref name="onColumns"/> once, then <paramref name="onRow"/> per row.</summary>
    void QueryEach(string sql, IReadOnlyDictionary<string, object?>? parameters, Action<IReadOnlyList<ResultColumn>> onColumns, Action<object?[]> onRow);

    /// <summary>
    /// Prepares a statement to run many times (for example one INSERT per imported row). It is written to the
    /// SQL log once, with the number of times it ran, when disposed. Failed runs are logged individually.
    /// </summary>
    IPreparedStatement Prepare(string sql);
}

/// <summary>A statement prepared by <see cref="ISqlRunner.Prepare"/>.</summary>
public interface IPreparedStatement : IDisposable
{
    /// <summary>Runs the statement with these parameter values and returns the rows affected.</summary>
    int Execute(IReadOnlyDictionary<string, object?> parameters);
}

/// <summary>Result of a write batch. <see cref="InsideUserTransaction"/> means it joined a transaction that is still open.</summary>
public sealed record BatchResult(int CommandCount, int RowsAffected, bool InsideUserTransaction);

/// <summary>A write batch failed and was rolled back. <see cref="CommandIndex"/> is the failing command.</summary>
public sealed class BatchExecutionException(int commandIndex, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int CommandIndex { get; } = commandIndex;
}

/// <summary>
/// One open SQLite database file. All statements go through this class so that
/// each one is written to the SQL log. Calls are serialized and run off the caller's thread.
/// </summary>
public sealed class DatabaseSession : IAsyncDisposable
{
    private const int SqliteInterrupt = 9;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISqlLog _log;
    private bool _disposed;

    private DatabaseSession(string filePath, bool isReadOnly, SqliteConnection connection, ISqlLog log)
    {
        FilePath = filePath;
        IsReadOnly = isReadOnly;
        _connection = connection;
        _log = log;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Full path of the database file.</summary>
    public string FilePath { get; }

    public string DisplayName => Path.GetFileName(FilePath);

    public bool IsReadOnly { get; }

    public bool IsOpen => !_disposed;

    internal static async Task<DatabaseSession> OpenAsync(
        string filePath, SqliteOpenMode mode, ISqlLog log, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = mode,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await Task.Run(connection.Open, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var session = new DatabaseSession(filePath, mode == SqliteOpenMode.ReadOnly, connection, log);
        try
        {
            // Reading the schema confirms that the file is a SQLite database.
            await session.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_schema", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>Runs a statement and returns the number of rows affected.</summary>
    public Task<int> ExecuteNonQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => RunAsync(sql, parameters, cmd => cmd.ExecuteNonQuery(), cancellationToken);

    /// <summary>Runs a statement and returns the first column of the first row.</summary>
    public Task<T?> ExecuteScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => RunAsync(sql, parameters, cmd => ConvertScalar<T>(cmd.ExecuteScalar()), cancellationToken);

    /// <summary>Runs a query and maps each row with <paramref name="map"/>.</summary>
    public Task<List<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        return RunAsync(sql, parameters, cmd =>
        {
            var results = new List<T>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(map(reader));
            }

            return results;
        }, cancellationToken);
    }

    /// <summary>Runs a query and returns its columns and raw row values.</summary>
    public Task<RowSet> QueryRowsAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => RunAsync(sql, parameters, cmd =>
        {
            using var reader = cmd.ExecuteReader();
            var columns = new ResultColumn[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));
            }

            var rows = new List<object?[]>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[columns.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    var value = reader.GetValue(i);
                    values[i] = value is DBNull ? null : value;
                }

                rows.Add(values);
            }

            return new RowSet(columns, rows);
        }, cancellationToken);

    /// <summary>
    /// Runs one statement. If it returns rows, reads up to <paramref name="maxRows"/> of them.
    /// Cancelling the token interrupts the statement with <c>sqlite3_interrupt</c>.
    /// </summary>
    public Task<StatementOutcome> ExecuteStatementAsync(string sql, int maxRows, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        return RunAsync(sql, null, cmd =>
        {
            using var reader = cmd.ExecuteReader();
            if (reader.FieldCount == 0)
            {
                // RecordsAffected is the change count of the last INSERT/UPDATE/DELETE, so only DML reports it.
                var keyword = FirstKeyword(sql);
                var isDml = keyword is "INSERT" or "UPDATE" or "DELETE" or "REPLACE" or "WITH";
                return new StatementOutcome(null, false, isDml ? reader.RecordsAffected : -1);
            }

            var columns = new ResultColumn[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));
            }

            var rows = new List<object?[]>();
            var truncated = false;
            while (reader.Read())
            {
                if (rows.Count == maxRows)
                {
                    truncated = true;
                    break;
                }

                var values = new object?[columns.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    var value = reader.GetValue(i);
                    values[i] = value is DBNull ? null : value;
                }

                rows.Add(values);
            }

            return new StatementOutcome(new RowSet(columns, rows), truncated, -1);
        }, cancellationToken);
    }

    /// <summary>
    /// Copies the open database to <paramref name="destinationPath"/> with SQLite's online backup, while other work waits.
    /// The copy is written to a temporary file first, so an existing file is only replaced by a complete backup.
    /// </summary>
    public Task BackupToAsync(string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(destination, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Choose a different file. A database cannot be backed up onto itself.");
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException($"The file '{destination}' already exists.");
        }

        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
        }

        return RunLockedAsync(() =>
        {
            if (raw.sqlite3_get_autocommit(_connection.Handle) == 0)
            {
                throw new InvalidOperationException("A backup cannot run while a transaction is open on this database. Commit or roll back first.");
            }

            var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            var stopwatch = Stopwatch.StartNew();
            var logText = $"-- online backup to {destination}";
            try
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
                using (var target = new SqliteConnection(builder.ToString()))
                {
                    target.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    _connection.BackupDatabase(target);
                }

                File.Move(temp, destination, overwrite: true);
                Log(logText, null, stopwatch.Elapsed, null);
                return 0;
            }
            catch (Exception ex)
            {
                Log(logText, null, stopwatch.Elapsed, ex.Message);
                TryDelete(temp);
                throw;
            }
        }, cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="commands"/> as one unit inside a SAVEPOINT. If a command fails, or affects a different
    /// number of rows than <see cref="BatchCommand.ExpectedRows"/>, everything is rolled back and a
    /// <see cref="BatchExecutionException"/> is thrown. If the connection is already inside a transaction
    /// (for example a BEGIN from a query tab), the batch joins it and is permanent only after that transaction commits.
    /// </summary>
    public Task<BatchResult> ExecuteBatchAsync(IReadOnlyList<BatchCommand> commands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return RunLockedAsync(() =>
        {
            var insideTransaction = raw.sqlite3_get_autocommit(_connection.Handle) == 0;
            Execute(SavepointSql, null, cmd => cmd.ExecuteNonQuery(), CancellationToken.None);
            var total = 0;
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                try
                {
                    var affected = Execute(command.Sql, command.Parameters, cmd => cmd.ExecuteNonQuery(), cancellationToken);
                    if (command.ExpectedRows is { } expected && affected != expected)
                    {
                        throw new BatchExecutionException(i,
                            $"Change {i + 1} of {commands.Count} affected {affected} rows instead of {expected}. " +
                            "The row may have been changed or deleted since it was loaded. Nothing was written.");
                    }

                    total += affected;
                }
                catch (Exception ex)
                {
                    Execute(RollbackSql, null, cmd => cmd.ExecuteNonQuery(), CancellationToken.None);
                    Execute(ReleaseSql, null, cmd => cmd.ExecuteNonQuery(), CancellationToken.None);
                    if (ex is BatchExecutionException or OperationCanceledException)
                    {
                        throw;
                    }

                    throw new BatchExecutionException(i, $"Change {i + 1} of {commands.Count} failed: {ex.Message} Nothing was written.", ex);
                }
            }

            Execute(ReleaseSql, null, cmd => cmd.ExecuteNonQuery(), CancellationToken.None);
            return new BatchResult(commands.Count, total, insideTransaction);
        }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the connection to itself, so a multi-step operation (such as a
    /// table rebuild) cannot interleave with other calls. Every statement it runs is logged.
    /// </summary>
    public Task<T> RunExclusiveAsync<T>(Func<ISqlRunner, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunLockedAsync(() => work(new Runner(this, cancellationToken)), cancellationToken);
    }

    private sealed class Runner(DatabaseSession session, CancellationToken cancellationToken) : ISqlRunner
    {
        public bool IsInTransaction => raw.sqlite3_get_autocommit(session._connection.Handle) == 0;

        public int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters = null) =>
            session.Execute(sql, parameters, cmd => cmd.ExecuteNonQuery(), cancellationToken);

        public int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
            return session.Execute(sql, parameters, cmd => cmd.ExecuteNonQuery(), linked.Token);
        }

        public void QueryEach(string sql, IReadOnlyDictionary<string, object?>? parameters, Action<IReadOnlyList<ResultColumn>> onColumns, Action<object?[]> onRow) =>
            session.Execute(sql, parameters, cmd =>
            {
                using var reader = cmd.ExecuteReader();
                var columns = new ResultColumn[reader.FieldCount];
                for (var i = 0; i < columns.Length; i++)
                {
                    columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));
                }

                onColumns(columns);
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new object?[columns.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        var value = reader.GetValue(i);
                        values[i] = value is DBNull ? null : value;
                    }

                    onRow(values);
                }

                return 0;
            }, cancellationToken);

        public IPreparedStatement Prepare(string sql) => new PreparedStatement(session, sql, cancellationToken);

        public IReadOnlyList<object?[]> Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null) =>
            session.Execute(sql, parameters, cmd =>
            {
                using var reader = cmd.ExecuteReader();
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object?[reader.FieldCount];
                    for (var i = 0; i < values.Length; i++)
                    {
                        var value = reader.GetValue(i);
                        values[i] = value is DBNull ? null : value;
                    }

                    rows.Add(values);
                }

                return rows;
            }, cancellationToken);
    }

    /// <summary>One command reused for every run. Logged once on dispose with the run count; failures are logged as they happen.</summary>
    private sealed class PreparedStatement(DatabaseSession session, string sql, CancellationToken cancellationToken) : IPreparedStatement
    {
        private readonly SqliteCommand _command = CreateCommand(session, sql);
        private readonly Stopwatch _elapsed = new();
        private long _runs;

        private static SqliteCommand CreateCommand(DatabaseSession session, string sql)
        {
            var command = session._connection.CreateCommand();
            command.CommandText = sql;
            return command;
        }

        public int Execute(IReadOnlyDictionary<string, object?> parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (name, value) in parameters)
            {
                if (_command.Parameters.Contains(name))
                {
                    _command.Parameters[name].Value = value ?? DBNull.Value;
                }
                else
                {
                    _command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
            }

            _elapsed.Start();
            try
            {
                var affected = _command.ExecuteNonQuery();
                _runs++;
                return affected;
            }
            catch (Exception ex)
            {
                session.Log(sql, parameters, TimeSpan.Zero, ex.Message);
                throw;
            }
            finally
            {
                _elapsed.Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_runs > 0)
            {
                session.Log($"{sql}   -- executed {_runs:N0} times", null, _elapsed.Elapsed, null);
            }

            _command.Dispose();
        }

        private bool _disposed;
    }

    private const string SavepointSql = "SAVEPOINT sqlite_manager_write";
    private const string RollbackSql = "ROLLBACK TO sqlite_manager_write";
    private const string ReleaseSql = "RELEASE sqlite_manager_write";

    private Task<T> RunAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        Func<SqliteCommand, T> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return RunLockedAsync(() => Execute(sql, parameters, body, cancellationToken), cancellationToken);
    }

    /// <summary>Runs <paramref name="work"/> on a pool thread while holding the connection lock.</summary>
    private async Task<T> RunLockedAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one statement and writes it to the SQL log. The caller holds the connection lock.</summary>
    private T Execute<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        Func<SqliteCommand, T> body,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        // Cancelling interrupts only the statement that this call is running.
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => raw.sqlite3_interrupt(_connection.Handle))
            : default;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = body(command);
            Log(sql, parameters, stopwatch.Elapsed, null);
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteInterrupt && cancellationToken.IsCancellationRequested)
        {
            Log(sql, parameters, stopwatch.Elapsed, "Cancelled");
            throw new OperationCanceledException("The statement was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            Log(sql, parameters, stopwatch.Elapsed, ex.Message);
            throw;
        }
    }

    private void Log(string sql, IReadOnlyDictionary<string, object?>? parameters, TimeSpan duration, string? error)
    {
        var parameterList = parameters is null
            ? (IReadOnlyList<KeyValuePair<string, object?>>)[]
            : parameters.ToArray();
        _log.Add(new SqlLogEntry(DateTime.Now, DisplayName, sql, parameterList, duration, error));
    }

    private static string FirstKeyword(string sql)
    {
        var statements = SqlScriptSplitter.Split(sql);
        return statements.Count == 0 ? string.Empty : statements[0].FirstKeyword;
    }

    private static T? ConvertScalar<T>(object? value)
    {
        if (value is null or DBNull)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
