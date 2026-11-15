using System.Diagnostics;
using SqliteManager.Core.Query;

namespace SqliteManager.Core.Tests;

public class QueryExecutorTests
{
    private static readonly QueryRunOptions Options = new(MaxRows: 1000);

    private sealed class ListProgress : IProgress<StatementResult>
    {
        public List<StatementResult> Items { get; } = [];

        public void Report(StatementResult value) => Items.Add(value);
    }

    [Fact]
    public async Task Runs_every_statement_and_returns_each_result_set()
    {
        await using var db = await TestDatabase.CreateAsync();
        var progress = new ListProgress();

        var summary = await new QueryExecutor(db.Session).ExecuteAsync(
            "CREATE TABLE t (id INTEGER, name TEXT);\n" +
            "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, NULL);\n" +
            "SELECT * FROM t ORDER BY id;\n" +
            "SELECT count(*) AS n FROM t;",
            Options, progress);

        Assert.True(summary.Succeeded);
        Assert.Equal(4, summary.StatementCount);
        Assert.Equal(4, summary.Results.Count);
        Assert.Equal(summary.Results, progress.Items);
        Assert.True(summary.ChangedSchema);

        var create = summary.Results[0];
        Assert.Null(create.Rows);
        Assert.Equal(-1, create.RowsAffected);

        var insert = summary.Results[1];
        Assert.Equal(3, insert.RowsAffected);
        Assert.Equal(2, insert.Line);

        var select = summary.Results[2];
        Assert.Equal(["id", "name"], select.Rows!.Columns.Select(c => c.Name));
        Assert.Equal(3, select.Rows.Rows.Count);
        Assert.Null(select.Rows.Rows[2][1]);
        Assert.False(select.Truncated);

        Assert.Equal(3L, summary.Results[3].Rows!.Rows[0][0]);
        Assert.Equal(3 + 3 + 1, summary.TotalRows); // Inserted + selected + counted.
    }

    [Fact]
    public async Task Ddl_after_dml_does_not_report_stale_rows_affected()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");

        var summary = await new QueryExecutor(db.Session).ExecuteAsync(
            "INSERT INTO t VALUES (1), (2); CREATE INDEX ix ON t (x); UPDATE t SET x = x + 1 WHERE x = 1; DELETE FROM t;",
            Options);

        Assert.Equal([2, -1, 1, 2], summary.Results.Select(r => r.RowsAffected));
    }

    [Fact]
    public async Task Stops_at_first_error_and_reports_the_line()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");
        var script = "INSERT INTO t VALUES (1);\n" +
                     "SELECT *\n" +
                     "FROM missing_table;\n" +
                     "INSERT INTO t VALUES (2);";

        var summary = await new QueryExecutor(db.Session).ExecuteAsync(script, Options);

        Assert.True(summary.Failed);
        Assert.Equal(2, summary.Results.Count);
        var error = summary.Results[1];
        Assert.Equal("no such table: missing_table", error.Error);
        Assert.Equal(2, error.Line);
        Assert.Equal(3, error.ErrorLine);
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task Syntax_error_line_uses_the_near_token()
    {
        await using var db = await TestDatabase.CreateAsync();

        // SQLite reads "b FORM" as a column alias and reports: near "t": syntax error.
        // "t" must match as a whole word, not the "t" in SELECT.
        var summary = await new QueryExecutor(db.Session).ExecuteAsync(
            "SELECT 1;\n\nSELECT a,\n  b\n  FORM t;",
            Options with { FirstLine = 10 });

        var error = summary.Results[1];
        Assert.Contains("syntax error", error.Error);
        Assert.Equal(12, error.Line);
        Assert.Equal(14, error.ErrorLine);
    }

    [Theory]
    [InlineData("SELECT x FROM t", "no such column: x", 7)]
    [InlineData("SELECT t.x FROM t", "no such column: t.x", 7)]
    [InlineData("SELECT 'x' FROM \"x\"", "no such table: x", 17)]
    [InlineData("SELECT 1", "some other message", null)]
    [InlineData("SELECT 1", "no such table: zzz", null)]
    public void FindErrorOffset(string statement, string message, int? expected)
    {
        Assert.Equal(expected, QueryExecutor.FindErrorOffset(statement, message));
    }

    [Fact]
    public async Task Row_limit_truncates_result_sets()
    {
        await using var db = await TestDatabase.CreateAsync();

        var summary = await new QueryExecutor(db.Session).ExecuteAsync(
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 50) SELECT x FROM n; SELECT 1 UNION ALL SELECT 2;",
            new QueryRunOptions(MaxRows: 10));

        Assert.Equal(10, summary.Results[0].Rows!.Rows.Count);
        Assert.True(summary.Results[0].Truncated);
        Assert.Equal(2, summary.Results[1].Rows!.Rows.Count);
        Assert.False(summary.Results[1].Truncated);
    }

    [Fact]
    public async Task Exactly_the_limit_is_not_truncated()
    {
        await using var db = await TestDatabase.CreateAsync();

        var summary = await new QueryExecutor(db.Session).ExecuteAsync("SELECT 1 UNION ALL SELECT 2", new QueryRunOptions(MaxRows: 2));

        Assert.False(summary.Results[0].Truncated);
    }

    [Fact]
    public async Task Transactions_span_runs_on_the_same_session()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");
        var executor = new QueryExecutor(db.Session);

        await executor.ExecuteAsync("BEGIN; INSERT INTO t VALUES (1);", Options);
        var inside = await executor.ExecuteAsync("SELECT count(*) FROM t", Options);
        await executor.ExecuteAsync("ROLLBACK", Options);
        var after = await executor.ExecuteAsync("SELECT count(*) FROM t", Options);

        Assert.Equal(1L, inside.Results[0].Rows!.Rows[0][0]);
        Assert.Equal(0L, after.Results[0].Rows!.Rows[0][0]);
    }

    [Fact]
    public async Task Empty_script_runs_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();

        var summary = await new QueryExecutor(db.Session).ExecuteAsync("  -- nothing\n", Options);

        Assert.Equal(0, summary.StatementCount);
        Assert.Empty(summary.Results);
        Assert.True(summary.Succeeded);
    }

    [Fact]
    public async Task Each_statement_is_logged()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Log.Clear();

        await new QueryExecutor(db.Session).ExecuteAsync("SELECT 1; SELECT 2;", Options);

        Assert.Equal(["SELECT 1", "SELECT 2"], db.Log.GetEntries().Select(e => e.Sql));
    }

    [Fact]
    public async Task Cancel_interrupts_a_long_query_and_the_session_stays_usable()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = new CancellationTokenSource();
        var progress = new ListProgress();
        const string endless = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) FROM n";

        var run = new QueryExecutor(db.Session).ExecuteAsync(endless + "; SELECT 2;", Options, progress, cts.Token);
        await Task.Delay(200);
        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();
        var summary = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(summary.Cancelled);
        var result = Assert.Single(summary.Results);
        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Contains(db.Log.GetEntries(), e => e.Error == "Cancelled");

        // The connection still works, and a later statement is not interrupted.
        var again = await new QueryExecutor(db.Session).ExecuteAsync("SELECT 42", Options);
        Assert.Equal(42L, again.Results[0].Rows!.Rows[0][0]);
    }

    [Fact]
    public async Task Already_cancelled_token_runs_nothing()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");

        var summary = await new QueryExecutor(db.Session).ExecuteAsync(
            "INSERT INTO t VALUES (1)", Options, cancellationToken: new CancellationToken(canceled: true));

        Assert.True(summary.Cancelled);
        Assert.Equal(0L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
    }
}
