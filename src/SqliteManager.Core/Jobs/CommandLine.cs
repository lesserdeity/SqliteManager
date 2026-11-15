using System.Text.Json;
using SqliteManager.Core.Logging;

namespace SqliteManager.Core.Jobs;

/// <summary>
/// The command-line runner:
/// <c>list</c> and <c>run --job "name" [--job ...] [--param @Name=value ...]</c>.
/// Exit code 0 when every job succeeds, 1 otherwise.
/// </summary>
public static class CommandLine
{
    public const string Usage = """
        SqliteManager.Cli - runs saved jobs.

        Usage:
          SqliteManager.Cli list
          SqliteManager.Cli run --job "Name" [--job "Other"] [--param @Name=value ...]

        Options:
          --job <name>          A job to run. Repeat to run several jobs in order.
          --param @Name=value   A parameter value. Repeat for more parameters.
          --jobs-file <path>    Use this jobs file instead of %APPDATA%\SqliteManager\jobs.json.
          --history-file <path> Write run history here instead of %APPDATA%\SqliteManager\history.jsonl.

        Exit code 0 when every job succeeds, 1 when a job fails.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        ParsedArguments parsed;
        try
        {
            parsed = Parse(args);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await error.WriteLineAsync().ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 1;
        }

        if (parsed.Command == "help")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        var store = new JobStore(parsed.JobsFile ?? JobStore.DefaultPath);
        JobFile jobs;
        try
        {
            jobs = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot read {store.FilePath}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (parsed.Command == "list")
        {
            if (jobs.Jobs.Count == 0)
            {
                await output.WriteLineAsync($"No saved jobs in {store.FilePath}.").ConfigureAwait(false);
                return 0;
            }

            var width = jobs.Jobs.Max(j => j.Name.Length);
            foreach (var job in jobs.Jobs)
            {
                await output.WriteLineAsync($"{job.Name.PadRight(width)}  {job.TypeName,-6}  {job.DatabasePath}").ConfigureAwait(false);
            }

            return 0;
        }

        var selected = new List<Job>();
        foreach (var name in parsed.Jobs)
        {
            var job = jobs.Find(name);
            if (job is null)
            {
                await error.WriteLineAsync($"No job is named \"{name}\". Run \"list\" to see the saved jobs.").ConfigureAwait(false);
                return 1;
            }

            selected.Add(job);
        }

        var history = new RunHistoryStore(parsed.HistoryFile ?? RunHistoryStore.DefaultPath);
        var runner = new JobRunner(jobs, history, new SqlLog(capacity: 100), new ConsoleHost(output));
        foreach (var job in selected)
        {
            var result = await runner.RunAsync(job, parsed.Parameters, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await error.WriteLineAsync($"Job \"{job.Name}\" failed: {result.Error}").ConfigureAwait(false);
                return 1;
            }
        }

        return 0;
    }

    public sealed record ParsedArguments(string Command, IReadOnlyList<string> Jobs, IReadOnlyDictionary<string, string> Parameters, string? JobsFile, string? HistoryFile);

    /// <summary>Reads the arguments. Raises <see cref="ArgumentException"/> with a message for the user.</summary>
    public static ParsedArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Give a command: list or run.");
        }

        var command = args[0].ToLowerInvariant();
        if (command is "help" or "-h" or "--help" or "/?" or "-?")
        {
            return new ParsedArguments("help", [], new Dictionary<string, string>(), null, null);
        }

        if (command is not ("list" or "run"))
        {
            throw new ArgumentException($"Unknown command \"{args[0]}\".");
        }

        var jobs = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? jobsFile = null, historyFile = null;
        for (var i = 1; i < args.Length; i++)
        {
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} needs a value.");
                }

                return args[++i];
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--job":
                    jobs.Add(Value());
                    break;
                case "--param":
                    var pair = Value();
                    var equals = pair.IndexOf('=');
                    if (equals <= 0)
                    {
                        throw new ArgumentException($"\"{pair}\" is not a parameter. Use --param @Name=value.");
                    }

                    parameters[JobRunner.Normalize(pair[..equals])] = pair[(equals + 1)..];
                    break;
                case "--jobs-file":
                    jobsFile = Value();
                    break;
                case "--history-file":
                    historyFile = Value();
                    break;
                default:
                    throw new ArgumentException($"Unknown option \"{args[i]}\".");
            }
        }

        if (command == "run" && jobs.Count == 0)
        {
            throw new ArgumentException("Name at least one job with --job \"name\".");
        }

        return new ParsedArguments(command, jobs, parameters, jobsFile, historyFile);
    }

    private sealed class ConsoleHost(TextWriter output) : IJobHost
    {
        private readonly Lock _sync = new();

        public void Report(string message)
        {
            lock (_sync)
            {
                output.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        public bool? ConfirmOverwrite(string path) => null;
    }
}
