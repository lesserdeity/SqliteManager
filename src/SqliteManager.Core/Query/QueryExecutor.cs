using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Query;

/// <summary>Options for one run of a script.</summary>
/// <param name="MaxRows">Most rows kept per result set.</param>
/// <param name="FirstLine">Editor line (1-based) where the script starts, so reported lines match the editor.</param>
public sealed record QueryRunOptions(int MaxRows, int FirstLine = 1);

/// <summary>The outcome of one statement in a script.</summary>
public sealed record StatementResult(
    int Index,
    SqlStatement Statement,
    int Line,
    RowSet? Rows,
    bool Truncated,
    int RowsAffected,
    TimeSpan Duration,
    string? Error,
    int? ErrorLine,
    bool Cancelled)
{
    public bool Succeeded => Error is null && !Cancelled;
}

/// <summary>The outcome of a whole script.</summary>
public sealed record QueryRunSummary(IReadOnlyList<StatementResult> Results, int StatementCount, TimeSpan Duration)
{
    public bool Cancelled => Results.Any(r => r.Cancelled);

    public bool Failed => Results.Any(r => r.Error is not null);

    public bool Succeeded => !Cancelled && !Failed;

    public bool ChangedSchema => Results.Any(r => r.Statement.ChangesSchema);

    /// <summary>Rows returned plus rows affected.</summary>
    public long TotalRows => Results.Sum(r => (long)(r.Rows?.Rows.Count ?? Math.Max(r.RowsAffected, 0)));
}

/// <summary>Runs a SQL script one statement at a time and stops at the first error.</summary>
public sealed partial class QueryExecutor
{
    private readonly DatabaseSession _session;

    public QueryExecutor(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Runs every statement in <paramref name="script"/>. Each result is reported to
    /// <paramref name="progress"/> as soon as it is ready.
    /// </summary>
    public async Task<QueryRunSummary> ExecuteAsync(
        string script,
        QueryRunOptions options,
        IProgress<StatementResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(options);

        var statements = SqlScriptSplitter.Split(script);
        var results = new List<StatementResult>();
        var total = Stopwatch.StartNew();

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            var line = options.FirstLine - 1 + statement.StartLine;
            var stopwatch = Stopwatch.StartNew();
            StatementResult result;

            if (cancellationToken.IsCancellationRequested)
            {
                result = new StatementResult(i + 1, statement, line, null, false, -1, TimeSpan.Zero, null, null, Cancelled: true);
            }
            else
            {
                try
                {
                    var outcome = await _session.ExecuteStatementAsync(statement.Text, options.MaxRows, cancellationToken)
                        .ConfigureAwait(false);
                    result = new StatementResult(i + 1, statement, line, outcome.Rows, outcome.Truncated, outcome.RowsAffected,
                        stopwatch.Elapsed, null, null, Cancelled: false);
                }
                catch (OperationCanceledException)
                {
                    result = new StatementResult(i + 1, statement, line, null, false, -1, stopwatch.Elapsed, null, null, Cancelled: true);
                }
                catch (Exception ex)
                {
                    var message = CleanMessage(ex);
                    var offset = FindErrorOffset(statement.Text, message);
                    var errorLine = offset is { } o ? line - 1 + SqlScriptSplitter.LineOf(statement.Text, o) : line;
                    result = new StatementResult(i + 1, statement, line, null, false, -1, stopwatch.Elapsed, message, errorLine, Cancelled: false);
                }
            }

            results.Add(result);
            progress?.Report(result);
            if (!result.Succeeded)
            {
                break;
            }
        }

        return new QueryRunSummary(results, statements.Count, total.Elapsed);
    }

    /// <summary>
    /// Finds where in <paramref name="statementText"/> an error message points, using the token SQLite names
    /// ("near \"x\"", "no such table: x", "no such column: x"). Returns null if the token is not found.
    /// </summary>
    public static int? FindErrorOffset(string statementText, string message)
    {
        ArgumentNullException.ThrowIfNull(statementText);
        ArgumentNullException.ThrowIfNull(message);

        var match = ErrorTokenPattern().Match(message);
        if (!match.Success)
        {
            return null;
        }

        var token = match.Groups["token"].Value.TrimEnd('.', '\'');
        if (token.Length == 0)
        {
            return null;
        }

        var index = IndexOfToken(statementText, token);
        if (index < 0 && token.Contains('.', StringComparison.Ordinal))
        {
            // "no such column: t.x" may be written as "t . x" or quoted; look for the column part.
            index = IndexOfToken(statementText, token[(token.LastIndexOf('.') + 1)..]);
        }

        return index < 0 ? null : index;
    }

    /// <summary>
    /// Finds <paramref name="token"/> as a whole word. Prefers a match outside strings and comments;
    /// a match inside a quoted name ("x", [x], `x`) counts as outside.
    /// </summary>
    private static int IndexOfToken(string text, string token)
    {
        var firstWholeWord = -1;
        var start = 0;
        while (start <= text.Length - token.Length)
        {
            var index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            start = index + 1;
            if (!IsWholeWord(text, index, token.Length))
            {
                continue;
            }

            if (firstWholeWord < 0)
            {
                firstWholeWord = index;
            }

            var quotedName = index > 0 && text[index - 1] is '"' or '[' or '`';
            if (quotedName || !SqlScriptSplitter.IsInsideLiteralOrComment(text, index + 1))
            {
                return index;
            }
        }

        return firstWholeWord;
    }

    private static bool IsWholeWord(string text, int index, int length)
    {
        var token = text.AsSpan(index, length);
        var startsWithWordChar = SqlScriptSplitter.IsIdentifierPart(token[0]);
        var endsWithWordChar = SqlScriptSplitter.IsIdentifierPart(token[^1]);
        var before = index > 0 && SqlScriptSplitter.IsIdentifierPart(text[index - 1]);
        var after = index + length < text.Length && SqlScriptSplitter.IsIdentifierPart(text[index + length]);
        return !(startsWithWordChar && before) && !(endsWithWordChar && after);
    }

    private static string CleanMessage(Exception ex)
    {
        if (ex is SqliteException sqlite)
        {
            // "SQLite Error 1: 'no such table: x'." -> "no such table: x"
            var match = SqliteMessagePattern().Match(sqlite.Message);
            if (match.Success)
            {
                return match.Groups["text"].Value;
            }
        }

        return ex.Message;
    }

    [GeneratedRegex("""(?:near "(?<token>[^"]*)"|no such (?:table|column|function|collation sequence): (?<token>\S+))""")]
    private static partial Regex ErrorTokenPattern();

    [GeneratedRegex("""^SQLite Error \d+: '(?<text>.*)'\.?$""", RegexOptions.Singleline)]
    private static partial Regex SqliteMessagePattern();
}
