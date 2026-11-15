using System.Diagnostics;
using SqliteManager.Core.Data;

namespace SqliteManager.Core.Tests;

public class TableDataServiceTests
{
    private static string FillNumbers(string table, int count) =>
        $"WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < {count}) " +
        $"INSERT INTO {table} SELECT x, 'row ' || x FROM n";

    private static TableQuery Filter(string column, string text) => new([new ColumnFilter(column, text)]);

    private static TableQuery Sort(string column, SortDirection direction) => new([], column, direction);

    private static async Task<List<TableRow>> AllRowsAsync(TableDataService service, TableSnapshot snapshot, int pageSize = 7)
    {
        var rows = new List<TableRow>();
        for (long offset = 0; offset < snapshot.RowCount; offset += pageSize)
        {
            rows.AddRange(await service.GetPageAsync(snapshot, offset, pageSize));
        }

        return rows;
    }

    [Fact]
    public async Task Open_counts_rows_and_picks_the_row_key()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)",
            FillNumbers("t", 1234),
            "CREATE TABLE w (id INTEGER PRIMARY KEY, name TEXT) WITHOUT ROWID",
            "CREATE VIEW v AS SELECT * FROM t WHERE id <= 10");
        var service = new TableDataService(db.Session);

        var t = await service.OpenAsync("t");
        var w = await service.OpenAsync("w");
        var v = await service.OpenAsync("v");

        Assert.Equal((1234L, 1234L, RowKeyKind.RowId, "rowid", false), (t.TotalRowCount, t.RowCount, t.KeyKind, t.RowIdName, t.IsView));
        Assert.Null(t.OrderedRowIds);
        Assert.Equal((0L, RowKeyKind.PrimaryKey, (string?)null), (w.RowCount, w.KeyKind, w.RowIdName));
        Assert.Equal([0], w.KeyColumnIndexes);
        Assert.Equal((10L, RowKeyKind.None, true), (v.RowCount, v.KeyKind, v.IsView));
    }

    [Fact]
    public async Task Rowid_name_avoids_real_columns()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE a (rowid TEXT, x)",
            "CREATE TABLE b (rowid TEXT, _rowid_ TEXT, oid TEXT)",
            "CREATE TABLE c (rowid TEXT, _rowid_ TEXT, oid TEXT, code TEXT PRIMARY KEY)");
        var service = new TableDataService(db.Session);

        Assert.Equal("_rowid_", (await service.OpenAsync("a")).RowIdName);
        Assert.Equal(RowKeyKind.None, (await service.OpenAsync("b")).KeyKind);
        var c = await service.OpenAsync("c");
        Assert.Equal(RowKeyKind.PrimaryKey, c.KeyKind);
        Assert.Equal([3], c.KeyColumnIndexes);
    }

    [Fact]
    public async Task Open_throws_for_unknown_table()
    {
        await using var db = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => new TableDataService(db.Session).OpenAsync("missing"));
    }

    [Fact]
    public async Task GetPage_returns_rows_in_rowid_order_with_keys()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)",
            FillNumbers("t", 1234));
        var service = new TableDataService(db.Session);
        var snapshot = await service.OpenAsync("t");

        var first = await service.GetPageAsync(snapshot, 0, 500);
        var last = await service.GetPageAsync(snapshot, 1000, 500);
        var beyond = await service.GetPageAsync(snapshot, 2000, 500);

        Assert.Equal(500, first.Count);
        Assert.Equal([1L, "row 1"], first[0].Values);
        Assert.Equal(new RowKey(1L), first[0].Key);
        Assert.Equal(234, last.Count);
        Assert.Equal(1234L, last[^1].Values[0]);
        Assert.Empty(beyond);
    }

    [Fact]
    public async Task Without_rowid_tables_use_primary_key_values_as_keys()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE w (a TEXT, b INTEGER, v TEXT, PRIMARY KEY (b, a)) WITHOUT ROWID",
            "INSERT INTO w VALUES ('x', 2, 'one'), ('y', 1, 'two')");
        var service = new TableDataService(db.Session);

        var rows = await service.GetPageAsync(await service.OpenAsync("w"), 0, 10);

        Assert.Equal([new RowKey(1L, "y"), new RowKey(2L, "x")], rows.Select(r => r.Key));
    }

    [Fact]
    public async Task Views_have_no_keys()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)",
            FillNumbers("t", 30),
            "CREATE VIEW v AS SELECT id * 10 AS big FROM t");
        var service = new TableDataService(db.Session);

        var rows = await service.GetPageAsync(await service.OpenAsync("v"), 0, 100);

        Assert.Equal(30, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Key));
    }

    [Fact]
    public async Task GetPage_returns_null_and_blob_values()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (a TEXT, b BLOB)",
            "INSERT INTO t VALUES (NULL, x'DEADBEEF')",
            "INSERT INTO t VALUES ('', NULL)");
        var service = new TableDataService(db.Session);

        var rows = await service.GetPageAsync(await service.OpenAsync("t"), 0, 10);

        Assert.Null(rows[0].Values[0]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, rows[0].Values[1]);
        Assert.Equal(string.Empty, rows[1].Values[0]);
        Assert.Null(rows[1].Values[1]);
    }

    [Fact]
    public async Task Works_with_names_that_need_quoting()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE \"a \"\"b\"\" c\" (\"my col\")",
            "INSERT INTO \"a \"\"b\"\" c\" VALUES (1), (2)");
        var service = new TableDataService(db.Session);

        var snapshot = await service.OpenAsync("a \"b\" c", Sort("my col", SortDirection.Descending));
        var rows = await service.GetPageAsync(snapshot, 0, 10);

        Assert.Equal([2L, 1L], rows.Select(r => r.Values[0]));
    }

    [Fact]
    public async Task Paging_uses_parameters_for_limit_and_offset()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");
        var service = new TableDataService(db.Session);
        var snapshot = await service.OpenAsync("t");
        db.Log.Clear();

        await service.GetPageAsync(snapshot, 100, 50);

        var entry = Assert.Single(db.Log.GetEntries());
        Assert.Equal("SELECT rowid, * FROM \"t\" ORDER BY rowid LIMIT @limit OFFSET @offset", entry.Sql);
        Assert.Contains(new KeyValuePair<string, object?>("@limit", 50), entry.Parameters);
        Assert.Contains(new KeyValuePair<string, object?>("@offset", 100L), entry.Parameters);
    }

    [Fact]
    public async Task Filter_and_sort_on_rowid_table_read_pages_by_rowid()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT, score INTEGER)",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100) INSERT INTO t SELECT x, 'n' || x, x % 10 FROM n");
        var service = new TableDataService(db.Session);
        var query = new TableQuery([new ColumnFilter("score", ">=8")], "id", SortDirection.Descending);

        var snapshot = await service.OpenAsync("t", query);
        var rows = await AllRowsAsync(service, snapshot);

        Assert.Equal(100, snapshot.TotalRowCount);
        Assert.Equal(20, snapshot.RowCount);
        Assert.NotNull(snapshot.OrderedRowIds);
        Assert.Equal(20, rows.Count);
        Assert.Equal(99L, rows[0].Values[0]);
        Assert.All(rows, r => Assert.True((long)r.Values[2]! >= 8));
        Assert.Equal(rows.Select(r => (long)r.Values[0]!).OrderDescending(), rows.Select(r => (long)r.Values[0]!));
        Assert.Equal(rows.Select(r => r.Key), rows.Select(r => new RowKey(r.Values[0])));
    }

    [Fact]
    public async Task Sorting_breaks_ties_by_key_so_paging_is_stable()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, grp INTEGER)",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 50) INSERT INTO t SELECT x, x % 3 FROM n",
            "CREATE TABLE w (id INTEGER PRIMARY KEY, grp INTEGER) WITHOUT ROWID",
            "INSERT INTO w SELECT * FROM t");
        var service = new TableDataService(db.Session);

        foreach (var table in new[] { "t", "w" })
        {
            var rows = await AllRowsAsync(service, await service.OpenAsync(table, Sort("grp", SortDirection.Ascending)), pageSize: 4);
            var expected = rows.OrderBy(r => (long)r.Values[1]!).ThenBy(r => (long)r.Values[0]!).Select(r => r.Values[0]);
            Assert.Equal(50, rows.Count);
            Assert.Equal(expected, rows.Select(r => r.Values[0]));
        }
    }

    [Fact]
    public async Task Filter_on_view_and_without_rowid_table_uses_offset_paging()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE w (id INTEGER PRIMARY KEY, name TEXT) WITHOUT ROWID",
            FillNumbers("w", 30),
            "CREATE VIEW v AS SELECT * FROM w");
        var service = new TableDataService(db.Session);

        foreach (var name in new[] { "w", "v" })
        {
            var snapshot = await service.OpenAsync(name, Filter("name", "row 2"));
            var rows = await AllRowsAsync(service, snapshot, pageSize: 3);

            Assert.Null(snapshot.OrderedRowIds);
            Assert.Equal(11, snapshot.RowCount); // row 2, row 20 .. row 29
            Assert.Equal(11, rows.Count);
        }
    }

    [Fact]
    public async Task Page_by_rowid_skips_rows_deleted_since_the_snapshot()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)", FillNumbers("t", 10));
        var service = new TableDataService(db.Session);
        var snapshot = await service.OpenAsync("t", Sort("id", SortDirection.Descending));

        await db.Session.ExecuteNonQueryAsync("DELETE FROM t WHERE id = 9");
        var rows = await service.GetPageAsync(snapshot, 0, 3);

        Assert.Equal([10L, 8L], rows.Select(r => r.Values[0]));
    }

    [Fact]
    public async Task A_million_row_table_opens_in_under_two_seconds()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE big (id INTEGER PRIMARY KEY, name TEXT, amount REAL, created TEXT)",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000000) " +
            "INSERT INTO big SELECT x, 'customer ' || x, x * 1.25, date('2020-01-01', '+' || (x % 2000) || ' days') FROM n");
        var service = new TableDataService(db.Session);

        var stopwatch = Stopwatch.StartNew();
        var snapshot = await service.OpenAsync("big");
        var firstPage = await service.GetPageAsync(snapshot, 0, 500);
        stopwatch.Stop();

        Assert.Equal(1_000_000, snapshot.RowCount);
        Assert.Equal(500, firstPage.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Opening took {stopwatch.Elapsed}.");

        // Jumping to the end (scrollbar dragged to the bottom) must also be fast.
        stopwatch.Restart();
        var lastPage = await service.GetPageAsync(snapshot, 999_500, 500);
        stopwatch.Stop();
        Assert.Equal(1_000_000L, lastPage[^1].Values[0]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Last page took {stopwatch.Elapsed}.");

        // Sorting on an unindexed column reads the ordered rowids once; then any page is fast.
        stopwatch.Restart();
        var sorted = await service.OpenAsync("big", Sort("created", SortDirection.Descending));
        var sortedOpen = stopwatch.Elapsed;
        stopwatch.Restart();
        var deepPage = await service.GetPageAsync(sorted, 900_000, 500);
        var deepPageTime = stopwatch.Elapsed;

        Assert.Equal(500, deepPage.Count);
        Assert.True(sortedOpen < TimeSpan.FromSeconds(5), $"Sorted open took {sortedOpen}.");
        Assert.True(deepPageTime < TimeSpan.FromSeconds(1), $"Deep sorted page took {deepPageTime}.");
    }
}
