using Microsoft.Data.Sqlite;
using SqliteManager.Core.Logging;

namespace SqliteManager.Core.Tests;

public class SqlLogTests
{
    private static SqlLogEntry Entry(string sql) =>
        new(DateTime.Now, "db", sql, [], TimeSpan.Zero, null);

    [Fact]
    public void Add_stores_entries_and_raises_event()
    {
        var log = new SqlLog();
        SqlLogEntry? raised = null;
        log.EntryAdded += (_, e) => raised = e;

        var entry = Entry("SELECT 1");
        log.Add(entry);

        Assert.Same(entry, raised);
        Assert.Equal([entry], log.GetEntries());
    }

    [Fact]
    public void Keeps_only_the_most_recent_entries()
    {
        var log = new SqlLog(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            log.Add(Entry($"SELECT {i}"));
        }

        Assert.Equal(["SELECT 2", "SELECT 3", "SELECT 4"], log.GetEntries().Select(e => e.Sql));
    }

    [Fact]
    public void Clear_removes_entries_and_raises_event()
    {
        var log = new SqlLog();
        var cleared = false;
        log.Cleared += (_, _) => cleared = true;
        log.Add(Entry("SELECT 1"));

        log.Clear();

        Assert.True(cleared);
        Assert.Empty(log.GetEntries());
    }

    [Fact]
    public async Task Add_is_thread_safe()
    {
        var log = new SqlLog(capacity: 100_000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                log.Add(Entry($"SELECT {t}, {i}"));
            }
        })));

        Assert.Equal(8000, log.GetEntries().Count);
    }

    [Fact]
    public async Task Session_logs_every_statement_with_parameters_and_duration()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Log.Clear();

        await db.Session.ExecuteNonQueryAsync("CREATE TABLE t (id INTEGER, name TEXT)");
        await db.Session.ExecuteNonQueryAsync(
            "INSERT INTO t VALUES (@id, @name)",
            new Dictionary<string, object?> { ["@id"] = 7, ["@name"] = null });
        await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t");
        await db.Session.QueryRowsAsync("SELECT * FROM t");
        await db.Session.QueryAsync("SELECT id FROM t", r => r.GetInt64(0));

        var entries = db.Log.GetEntries();
        Assert.Equal(
            [
                "CREATE TABLE t (id INTEGER, name TEXT)",
                "INSERT INTO t VALUES (@id, @name)",
                "SELECT count(*) FROM t",
                "SELECT * FROM t",
                "SELECT id FROM t",
            ],
            entries.Select(e => e.Sql));
        Assert.All(entries, e => Assert.True(e.Succeeded));
        Assert.All(entries, e => Assert.Equal("test.db", e.Database));
        Assert.All(entries, e => Assert.True(e.Duration >= TimeSpan.Zero));

        var insert = entries[1];
        Assert.Equal(2, insert.Parameters.Count);
        Assert.Equal(new KeyValuePair<string, object?>("@id", 7), insert.Parameters[0]);
        Assert.Equal(new KeyValuePair<string, object?>("@name", null), insert.Parameters[1]);
    }

    [Fact]
    public async Task Session_logs_failed_statements_with_error()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Log.Clear();

        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("SELECT * FROM missing_table"));

        var entry = Assert.Single(db.Log.GetEntries());
        Assert.False(entry.Succeeded);
        Assert.Contains("missing_table", entry.Error);
    }

    [Fact]
    public async Task Opening_a_database_is_logged()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.Contains(db.Log.GetEntries(), e => e.Sql.Contains("sqlite_schema", StringComparison.Ordinal));
    }
}
