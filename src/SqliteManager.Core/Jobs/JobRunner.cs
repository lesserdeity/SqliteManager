using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Sql;
using SqliteManager.Core.Transfer;

namespace SqliteManager.Core.Jobs;

/// <summary>What the app or the command line provides while jobs run.</summary>
public interface IJobHost
{
    /// <summary>A progress or result line for the user.</summary>
    void Report(string message);

    /// <summary>
    /// Asks whether an existing export file may be replaced (jobs set to "prompt"). Return null when
    /// the host cannot ask (the command line); the export then fails.
    /// </summary>
    bool? ConfirmOverwrite(string path);
}

public sealed record JobRunResult(string Job, bool Succeeded, long Rows, string? Error, TimeSpan Duration, bool Cancelled = false);

/// <summary>A required job parameter has no value.</summary>
public sealed class MissingParameterException(string name, string? prompt)
    : Exception($"Parameter {name}{(string.IsNullOrWhiteSpace(prompt) ? string.Empty : $" ({prompt})")} needs a value.")
{
    public string Name { get; } = name;
}

/// <summary>
/// Runs saved jobs. Each job opens its own connection to its database. Inside a script job with
/// transactionMode "single", a called job on the same database joins the script's transaction.
/// Every run (and every script step) is appended to the run history.
/// </summary>
public sealed class JobRunner
{
    private readonly JobFile _jobs;
    private readonly RunHistoryStore? _history;
    private readonly ISqlLog _log;
    private readonly IJobHost _host;

    public JobRunner(JobFile jobs, RunHistoryStore? history, ISqlLog log, IJobHost host)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(host);
        _jobs = jobs;
        _history = history;
        _log = log;
        _host = host;
    }

    /// <summary>
    /// Names of the parameters a job needs, including those of jobs it calls, with the job's prompt and default.
    /// </summary>
    public IReadOnlyList<JobParameter> ParametersOf(Job job)
    {
        var result = new List<JobParameter>();
        void Collect(Job j, HashSet<string> seen)
        {
            if (!seen.Add(j.Name))
            {
                return;
            }

            foreach (var p in j.Parameters ?? [])
            {
                if (!result.Any(r => Normalize(r.Name).Equals(Normalize(p.Name), StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(p);
                }
            }

            if (j is ScriptJob script)
            {
                foreach (var step in script.Steps.OfType<RunJobStep>())
                {
                    if (_jobs.Find(step.Job) is { } child)
                    {
                        Collect(child, seen);
                    }
                }
            }
        }

        Collect(job, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Runs a job on a worker thread. Failures are returned, not thrown.</summary>
    public Task<JobRunResult> RunAsync(Job job, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);
        var values = parameters.ToDictionary(p => Normalize(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        return Task.Run(() => Run(job, values, [], null, null, null, cancellationToken), CancellationToken.None);
    }

    private JobRunResult Run(Job job, Dictionary<string, string> values, List<string> stack, ISqlRunner? shared, string? sharedPath, string? parent, CancellationToken ct)
    {
        var start = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        _host.Report($"Running {job.Name}...");
        try
        {
            if (stack.Contains(job.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Job \"{job.Name}\" calls itself ({string.Join(" → ", stack.Append(job.Name))}).");
            }

            stack.Add(job.Name);
            try
            {
                var parameters = Resolve(job, values);
                var path = Path.GetFullPath(job.DatabasePath);
                long rows;
                if (shared is not null && string.Equals(path, sharedPath, StringComparison.OrdinalIgnoreCase))
                {
                    rows = Execute(job, shared, path, values, parameters, stack, ct);
                }
                else
                {
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException($"The database \"{path}\" does not exist.", path);
                    }

                    var manager = new DatabaseManager(_log);
                    var session = manager.OpenAsync(path, cancellationToken: ct).GetAwaiter().GetResult();
                    try
                    {
                        rows = session.RunExclusiveAsync(runner => Execute(job, runner, path, values, parameters, stack, ct), ct).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }

                var result = new JobRunResult(job.Name, true, rows, null, stopwatch.Elapsed);
                Record(result, start, parent);
                _host.Report($"{job.Name}: done, {rows:N0} row(s) in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
                return result;
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }
        catch (OperationCanceledException)
        {
            var result = new JobRunResult(job.Name, false, 0, "Cancelled.", stopwatch.Elapsed, Cancelled: true);
            Record(result, start, parent);
            _host.Report($"{job.Name}: cancelled.");
            return result;
        }
        catch (Exception ex)
        {
            var result = new JobRunResult(job.Name, false, 0, ex.Message, stopwatch.Elapsed);
            Record(result, start, parent);
            _host.Report($"{job.Name}: FAILED. {ex.Message}");
            return result;
        }
    }

    private long Execute(Job job, ISqlRunner runner, string path, Dictionary<string, string> values, Dictionary<string, object?> parameters, List<string> stack, CancellationToken ct)
    {
        switch (job)
        {
            case ImportJob import:
                var imported = ImportEngine.Run(runner, import, null, ct);
                if (imported.Skipped > 0)
                {
                    _host.Report($"{job.Name}: skipped {imported.Skipped:N0} row(s); see {imported.ErrorFile}.");
                }

                return imported.RowsWritten;
            case ExportJob export:
                var exported = ExportEngine.Run(runner, export, parameters, _host.ConfirmOverwrite, null, ct);
                _host.Report($"{job.Name}: wrote {exported.FilePath}.");
                return exported.Rows;
            case ScriptJob script:
                return RunScript(script, runner, path, values, parameters, stack, ct);
            default:
                throw new InvalidOperationException($"Unknown job type for \"{job.Name}\".");
        }
    }

    private long RunScript(ScriptJob script, ISqlRunner runner, string path, Dictionary<string, string> values, Dictionary<string, object?> parameters, List<string> stack, CancellationToken ct)
    {
        var single = script.TransactionMode == TransactionMode.Single;
        var nested = runner.IsInTransaction;
        if (single)
        {
            runner.Execute(nested ? "SAVEPOINT sqlite_manager_script" : "BEGIN");
        }

        long total = 0;
        try
        {
            foreach (var step in script.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var label = $"{script.Name} › {step.Name}";
                var start = DateTime.Now;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    long rows;
                    if (step is RunJobStep runJob)
                    {
                        var child = _jobs.Find(runJob.Job) ?? throw new InvalidOperationException($"No job is named \"{runJob.Job}\".");
                        var childResult = Run(child, values, stack, single ? runner : null, single ? path : null, script.Name, ct);
                        if (childResult.Cancelled)
                        {
                            throw new OperationCanceledException();
                        }

                        if (!childResult.Succeeded)
                        {
                            throw new InvalidOperationException($"Job \"{child.Name}\" failed: {childResult.Error}");
                        }

                        rows = childResult.Rows;
                    }
                    else
                    {
                        rows = RunSqlStep((SqlStep)step, runner, parameters, ct);
                    }

                    total += rows;
                    _history?.Append(new RunRecord(label, start, DateTime.Now, rows, RunRecord.Succeeded, null, script.Name));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _history?.Append(new RunRecord(label, start, DateTime.Now, 0, RunRecord.Failed, ex.Message, script.Name));
                    var onError = step switch
                    {
                        SqlStep s => s.OnError,
                        RunJobStep r => r.OnError ?? StepErrorMode.Stop,
                        _ => StepErrorMode.Stop,
                    };
                    if (onError == StepErrorMode.Stop)
                    {
                        throw new InvalidOperationException($"Step \"{step.Name}\" failed: {ex.Message}", ex);
                    }

                    _host.Report($"{label}: failed, continuing. {ex.Message}");
                    if (single && !runner.IsInTransaction)
                    {
                        throw new InvalidOperationException($"Step \"{step.Name}\" failed and SQLite ended the transaction, so the job cannot continue.", ex);
                    }
                }

                _host.Report($"{label}: {(stopwatch.Elapsed.TotalSeconds < 0.05 ? "done" : $"done in {stopwatch.Elapsed.TotalSeconds:0.0} s")}.");
            }

            if (single)
            {
                runner.Execute(nested ? "RELEASE sqlite_manager_script" : "COMMIT");
            }
        }
        catch
        {
            if (single && runner.IsInTransaction)
            {
                if (nested)
                {
                    runner.Execute("ROLLBACK TO sqlite_manager_script");
                    runner.Execute("RELEASE sqlite_manager_script");
                }
                else
                {
                    runner.Execute("ROLLBACK");
                }
            }

            throw;
        }

        return total;
    }

    private static long RunSqlStep(SqlStep step, ISqlRunner runner, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        var sql = step.Sql;
        if (string.IsNullOrWhiteSpace(sql))
        {
            if (string.IsNullOrWhiteSpace(step.SqlFile))
            {
                throw new InvalidOperationException("The step has no SQL.");
            }

            sql = File.ReadAllText(step.SqlFile);
        }

        using var timeout = step.TimeoutSeconds is > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(step.TimeoutSeconds.Value)) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        long rows = 0;
        foreach (var statement in SqlScriptSplitter.Split(sql))
        {
            try
            {
                rows += Math.Max(0, runner.Execute(statement.Text, parameters, linked.Token));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The step took longer than {step.TimeoutSeconds} second(s) and was stopped.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 9 && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The step took longer than {step.TimeoutSeconds} second(s) and was stopped.");
            }
        }

        return rows;
    }

    /// <summary>Values for the job's parameters: given value, otherwise the default. Missing required values raise <see cref="MissingParameterException"/>.</summary>
    private static Dictionary<string, object?> Resolve(Job job, Dictionary<string, string> values)
    {
        var result = values.ToDictionary(v => v.Key, v => (object?)v.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in job.Parameters ?? [])
        {
            var name = Normalize(parameter.Name);
            if (result.ContainsKey(name))
            {
                continue;
            }

            if (string.IsNullOrEmpty(parameter.Default))
            {
                throw new MissingParameterException(name, parameter.Prompt);
            }

            result[name] = parameter.Default;
            values[name] = parameter.Default;
        }

        return result;
    }

    /// <summary>Parameter names always start with @.</summary>
    public static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith('@') || trimmed.StartsWith(':') || trimmed.StartsWith('$') ? trimmed : "@" + trimmed;
    }

    private void Record(JobRunResult result, DateTime start, string? parent) =>
        _history?.Append(new RunRecord(
            result.Job,
            start,
            DateTime.Now,
            result.Rows,
            result.Succeeded ? RunRecord.Succeeded : result.Cancelled ? RunRecord.Cancelled : RunRecord.Failed,
            result.Error,
            parent));
}
