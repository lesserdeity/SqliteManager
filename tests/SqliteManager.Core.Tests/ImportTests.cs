using System.Diagnostics;
using System.Text;
using ClosedXML.Excel;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Transfer;

namespace SqliteManager.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string _folder = TestDatabase.NewFolder();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private static ImportJob Job(string path, string table, Action<ImportJob>? configure = null)
    {
        var job = new ImportJob { Name = "test", SourcePath = path, SourceFormat = SourceReaders.FormatFromExtension(path), TargetTable = table, CreateTableIfMissing = true };
        configure?.Invoke(job);
        return job;
    }

    private static async Task<List<object?[]>> RowsAsync(TestDatabase db, string sql) => (await db.Session.QueryRowsAsync(sql)).Rows.ToList();

    // ---- Readers --------------------------------------------------------------------------

    [Fact]
    public void Csv_reader_handles_quotes_header_and_short_rows()
    {
        var path = WriteFile("a.csv", "id,name,note\r\n1,\"Smith, Ann\",\"said \"\"hi\"\"\"\r\n\r\n2,Bob\r\n3,\"multi\nline\",x\r\n");

        using var reader = SourceReaders.Open(path, SourceFormat.Csv, null);
        var rows = reader.ReadRows().ToList();

        Assert.Equal(["id", "name", "note"], reader.Columns);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "Smith, Ann", "said \"hi\""], rows[0].Values);
        Assert.Equal(["2", "Bob", null], rows[1].Values);
        Assert.Equal("multi\nline", rows[2].Values[1]);
        Assert.Equal(2, rows[0].Number);
    }

    [Fact]
    public void Csv_options_delimiter_quote_encoding_skip_and_no_header()
    {
        var path = WriteFile("b.csv", "report line\r\nsecond junk line\r\n'Zoë';'a;b'\r\n'Ève';x\r\n", Encoding.GetEncoding(1252) is { } e ? e : Encoding.Latin1);

        var options = new SourceOptions { Delimiter = ";", Quote = "'", Encoding = "windows-1252", SkipRows = 2, HasHeader = false };
        using var reader = SourceReaders.Open(path, SourceFormat.Csv, options);
        var rows = reader.ReadRows().ToList();

        Assert.Equal(["Column1", "Column2"], reader.Columns);
        Assert.Equal(["Zoë", "a;b"], rows[0].Values);
        Assert.Equal(["Ève", "x"], rows[1].Values);
    }

    [Fact]
    public void Csv_reader_flags_rows_with_extra_fields_and_cleans_header_names()
    {
        var path = WriteFile("c.csv", "a,,a\n1,2,3,4\n");

        using var reader = SourceReaders.Open(path, SourceFormat.Csv, null);
        var row = reader.ReadRows().Single();

        Assert.Equal(["a", "Column2", "a_2"], reader.Columns);
        Assert.Contains("4 fields", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Tsv_reader_uses_tabs()
    {
        var path = WriteFile("d.tsv", "x\ty\n1,5\t2\n");

        using var reader = SourceReaders.Open(path, SourceFormat.Tsv, null);

        Assert.Equal(["1,5", "2"], reader.ReadRows().Single().Values);
    }

    [Fact]
    public void Xlsx_reader_reads_typed_cells_sheet_and_start_cell()
    {
        var path = Path.Combine(_folder, "e.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("First").Cell(1, 1).Value = "ignored";
            var ws = wb.AddWorksheet("Data");
            ws.Cell("B3").Value = "id";
            ws.Cell("C3").Value = "when";
            ws.Cell("D3").Value = "ok";
            ws.Cell("B4").Value = 7;
            ws.Cell("C4").Value = new DateTime(2025, 3, 4);
            ws.Cell("D4").Value = true;
            ws.Cell("B6").Value = 8.5;
            wb.SaveAs(path);
        }

        Assert.Equal(["First", "Data"], SourceReaders.ListSheets(path, SourceFormat.Xlsx));
        using var reader = SourceReaders.Open(path, SourceFormat.Xlsx, new SourceOptions { SheetName = "data", StartCell = "B3" });
        var rows = reader.ReadRows().ToList();

        Assert.Equal(["id", "when", "ok"], reader.Columns);
        Assert.Equal(2, rows.Count); // The empty row 5 is skipped.
        Assert.Equal([7.0, new DateTime(2025, 3, 4), true], rows[0].Values);
        Assert.Equal((6L, 8.5), (rows[1].Number, rows[1].Values[0]));
    }

    [Fact]
    public void Xls_reader_reads_the_fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reps.xls");

        Assert.Equal(["Reps", "Other"], SourceReaders.ListSheets(path, SourceFormat.Xls));
        using var reader = SourceReaders.Open(path, SourceFormat.Xls, null);
        var rows = reader.ReadRows().ToList();
        Assert.Equal(["Rep ID", "Name", "Hired", "Quota"], reader.Columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal(101.0, rows[0].Values[0]);
        Assert.Equal("Zoë", rows[1].Values[1]);
        Assert.Equal(new DateTime(2024, 1, 15), rows[0].Values[2]);

        using var other = SourceReaders.Open(path, SourceFormat.Xls, new SourceOptions { SheetName = "Other", StartCell = "B3" });
        Assert.Equal(["code", "qty"], other.Columns);
        Assert.Equal(["A1", 5.0], other.ReadRows().Single().Values);
    }

    [Fact]
    public void Json_reader_reads_an_array_of_objects()
    {
        var path = WriteFile("f.json", """[{"id": 1, "name": "Ann", "tags": ["a"], "ok": true}, {"id": 2.5, "extra": null, "name": null}]""");

        using var reader = SourceReaders.Open(path, SourceFormat.Json, null);
        var rows = reader.ReadRows().ToList();

        Assert.Equal(["id", "name", "tags", "ok", "extra"], reader.Columns);
        Assert.Equal([1L, "Ann", "[\"a\"]", true, null], rows[0].Values);
        Assert.Equal([2.5, null, null, null, null], rows[1].Values);
        Assert.Throws<FormatException>(() => SourceReaders.Open(WriteFile("g.json", "{\"a\": 1}"), SourceFormat.Json, null));
    }

    [Theory]
    [InlineData("A1", 1, 1)]
    [InlineData("b3", 3, 2)]
    [InlineData("AA10", 10, 27)]
    [InlineData("", 1, 1)]
    public void ParseCell(string text, int row, int column)
    {
        Assert.Equal((row, column), SourceReaders.ParseCell(text));
    }

    [Fact]
    public void ParseCell_rejects_bad_references()
    {
        Assert.Throws<FormatException>(() => SourceReaders.ParseCell("3B"));
    }

    // ---- Conversion and type inference ----------------------------------------------------

    [Theory]
    [InlineData("42", "INTEGER", 42L)]
    [InlineData("1,234", "INTEGER", 1234L)]
    [InlineData("2.50", "REAL", 2.5)]
    [InlineData("7", "REAL", 7.0)]
    [InlineData("3.0", "NUMERIC", 3L)]
    [InlineData("  ", "INTEGER", null)]
    [InlineData("", "TEXT", "")]
    [InlineData("007", "TEXT", "007")]
    [InlineData("x", "", "x")]
    public void Converts_invariant_values(string raw, string type, object? expected)
    {
        Assert.Equal(expected, new ImportValueConverter(null).Convert(raw, type));
    }

    [Fact]
    public void Converts_with_culture_and_date_formats()
    {
        var de = new ImportValueConverter(new SourceOptions { NumberCulture = "de-DE", DateFormat = "dd.MM.yyyy;dd.MM.yyyy HH:mm" });

        Assert.Equal(1234.5, de.Convert("1.234,5", "REAL"));
        Assert.Equal("2026-10-31", de.Convert("31.10.2026", "DATE"));
        Assert.Equal("2026-10-31 14:05:00", de.Convert("31.10.2026 14:05", "DATETIME"));
        Assert.Throws<FormatException>(() => de.Convert("2026-10-31", "DATE"));
        Assert.Throws<FormatException>(() => de.Convert("abc", "INTEGER"));
    }

    [Fact]
    public void Converts_typed_values()
    {
        var c = new ImportValueConverter(null);

        Assert.Equal("42", c.Convert(42.0, "TEXT"));
        Assert.Equal("2.5", c.Convert(2.5, "TEXT"));
        Assert.Equal(42L, c.Convert(42.0, "INTEGER"));
        Assert.Equal(1L, c.Convert(true, "INTEGER"));
        Assert.Equal("2025-03-04", c.Convert(new DateTime(2025, 3, 4), "TEXT"));
        Assert.Equal("2025-03-04 10:30:00", c.Convert(new DateTime(2025, 3, 4, 10, 30, 0), "DATE"));
        Assert.Equal("2024-01-15", c.Convert(45306.0, "DATE")); // Excel serial date.
        Assert.Equal("2026-01-02", c.Convert("2026-01-02", "DATE"));
    }

    [Theory]
    [InlineData(new[] { "1", "2", "" }, "INTEGER")]
    [InlineData(new[] { "1", "2.5" }, "REAL")]
    [InlineData(new[] { "007", "8" }, "TEXT")]
    [InlineData(new[] { "2026-01-02", "2026-03-04" }, "DATE")]
    [InlineData(new[] { "2026-01-02", "2026-03-04 10:00:00" }, "DATETIME")]
    [InlineData(new[] { "1", "x" }, "TEXT")]
    [InlineData(new string[0], "TEXT")]
    public void Infers_column_types(string[] values, string expected)
    {
        Assert.Equal(expected, new ImportValueConverter(null).InferType(values));
    }

    // ---- Import engine --------------------------------------------------------------------

    [Fact]
    public async Task Creates_the_table_with_inferred_types()
    {
        var path = WriteFile("p.csv", "id,name,score,joined,code\n1,Ann,1.5,2024-01-02,007\n2,Bob,,2024-02-03,010\n");
        await using var db = await TestDatabase.CreateAsync();

        var result = await ImportEngine.RunAsync(db.Session, Job(path, "people"));

        Assert.Equal((2L, 2L, true), (result.RowsRead, result.Inserted, result.CreatedTable));
        var types = await RowsAsync(db, "SELECT name, type FROM pragma_table_info('people') ORDER BY cid");
        Assert.Equal(["INTEGER", "TEXT", "REAL", "DATE", "TEXT"], types.Select(t => t[1]));
        var rows = await RowsAsync(db, "SELECT id, name, score, joined, code, typeof(id) FROM people ORDER BY id");
        Assert.Equal([1L, "Ann", 1.5, "2024-01-02", "007", "integer"], rows[0]);
        Assert.Null(rows[1][2]);
    }

    [Fact]
    public async Task Matches_columns_by_name_and_maps_with_defaults()
    {
        var path = WriteFile("m.csv", "Rep ID,Name,Ignored\n1,Ann,x\n2,Bob,y\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE reps (rep_id INTEGER PRIMARY KEY, rep_name TEXT, region TEXT, name TEXT)");

        await ImportEngine.RunAsync(db.Session, Job(path, "reps", j => j.ColumnMappings =
        [
            new ColumnMapping { Source = "Rep ID", Target = "rep_id" },
            new ColumnMapping { Source = "Name", Target = "rep_name" },
            new ColumnMapping { Target = "region", Default = "EU" },
        ]));
        await ImportEngine.RunAsync(db.Session, Job(WriteFile("n.csv", "NAME\nCy\n"), "reps")); // By name: NAME -> name.

        var rows = await RowsAsync(db, "SELECT rep_id, rep_name, region, name FROM reps ORDER BY rep_id");
        Assert.Equal([1L, "Ann", "EU", null], rows[0]);
        Assert.Equal("Cy", rows[2][3]);
    }

    [Fact]
    public async Task Reports_unknown_columns_and_missing_tables()
    {
        var path = WriteFile("u.csv", "a\n1\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");

        var noSource = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.ColumnMappings = [new ColumnMapping { Source = "zz", Target = "x" }])));
        var noTarget = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.ColumnMappings = [new ColumnMapping { Source = "a", Target = "zz" }])));
        var noMatch = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t")));
        var noTable = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "missing", j => j.CreateTableIfMissing = false)));

        Assert.Contains("no column \"zz\"", noSource.Message, StringComparison.Ordinal);
        Assert.Contains("has no column \"zz\"", noTarget.Message, StringComparison.Ordinal);
        Assert.Contains("Map the columns", noMatch.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", noTable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_truncate_and_replace()
    {
        var path = WriteFile("r.csv", "id,v\n1,a\n2,b\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER, v TEXT, extra TEXT DEFAULT 'keep')", "INSERT INTO t VALUES (9, 'old', 'z')");

        await ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.LoadMode = LoadMode.Append));
        Assert.Equal(3L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));

        await ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.LoadMode = LoadMode.Truncate));
        Assert.Equal([[1L, "a", "keep"], [2L, "b", "keep"]], await RowsAsync(db, "SELECT * FROM t ORDER BY id"));

        await ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.LoadMode = LoadMode.Replace));
        Assert.Equal(["id", "v"], (await RowsAsync(db, "SELECT name FROM pragma_table_info('t')")).Select(r => r[0]));
    }

    [Theory]
    [InlineData("CREATE TABLE reps (rep_id INTEGER PRIMARY KEY, name TEXT, quota REAL)")]
    [InlineData("CREATE TABLE reps (rep_id TEXT, name TEXT, quota REAL)")] // No unique index: update, then insert.
    public async Task Upsert_updates_matches_and_inserts_the_rest(string createTable)
    {
        var path = WriteFile("up.csv", "rep_id,name,quota\n1,Ann B,10\n3,Cy,30\n");
        await using var db = await TestDatabase.CreateAsync(createTable, "INSERT INTO reps VALUES (1, 'Ann', 1), (2, 'Bob', 2)");

        var result = await ImportEngine.RunAsync(db.Session, Job(path, "reps", j =>
        {
            j.LoadMode = LoadMode.Upsert;
            j.KeyColumns = ["rep_id"];
        }));

        Assert.Equal(2, result.RowsWritten);
        var rows = await RowsAsync(db, "SELECT CAST(rep_id AS INTEGER), name, quota FROM reps ORDER BY 1");
        Assert.Equal([[1L, "Ann B", 10.0], [2L, "Bob", 2.0], [3L, "Cy", 30.0]], rows);
    }

    [Fact]
    public async Task Upsert_with_a_unique_index_uses_on_conflict()
    {
        var path = WriteFile("uq.csv", "a,b,v\n1,1,new\n1,2,ins\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a INT, b INT, v TEXT)", "CREATE UNIQUE INDEX ux ON t (b, a)", "INSERT INTO t VALUES (1, 1, 'old')");
        db.Log.Clear();

        await ImportEngine.RunAsync(db.Session, Job(path, "t", j =>
        {
            j.LoadMode = LoadMode.Upsert;
            j.KeyColumns = ["a", "b"];
        }));

        Assert.Equal([[1L, 1L, "new"], [1L, 2L, "ins"]], await RowsAsync(db, "SELECT * FROM t ORDER BY b"));
        Assert.Contains(db.Log.GetEntries(), e => e.Sql.Contains("ON CONFLICT (\"a\", \"b\") DO UPDATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_requires_mapped_key_columns()
    {
        var path = WriteFile("k.csv", "x\n1\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x, y)");

        await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.LoadMode = LoadMode.Upsert)));
        await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j =>
        {
            j.LoadMode = LoadMode.Upsert;
            j.KeyColumns = ["y"];
        })));
    }

    [Fact]
    public async Task Rollback_mode_stops_at_the_first_bad_row_and_keeps_nothing()
    {
        var path = WriteFile("bad.csv", "id,qty\n1,5\n2,lots\n3,7\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER, qty INTEGER)", "INSERT INTO t VALUES (0, 0)");

        var ex = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.PreSql = "DELETE FROM t")));

        Assert.Contains("Row 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"lots\" is not a number", ex.Message, StringComparison.Ordinal);
        Assert.Equal([[0L, 0L]], await RowsAsync(db, "SELECT * FROM t")); // Pre-SQL was rolled back too.
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Skip_mode_writes_bad_rows_to_an_error_file()
    {
        var path = WriteFile("skip.csv", "id,qty\n1,5\n2,lots\n1,6\n3,7\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, qty INTEGER)");

        var result = await ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.OnError = ErrorMode.Skip));

        Assert.Equal((4L, 2L, 2L), (result.RowsRead, result.Inserted, result.Skipped));
        Assert.Equal([[1L, 5L], [3L, 7L]], await RowsAsync(db, "SELECT * FROM t ORDER BY id"));
        Assert.NotNull(result.ErrorFile);
        Assert.StartsWith(Path.Combine(_folder, "skip.errors_"), result.ErrorFile);
        var lines = await File.ReadAllLinesAsync(result.ErrorFile);
        Assert.Equal("Row,Error,id,qty", lines[0]);
        Assert.StartsWith("3,", lines[1]);
        Assert.Contains("not a number", lines[1], StringComparison.Ordinal);
        Assert.Contains("UNIQUE", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_sql_runs_in_the_same_transaction()
    {
        var path = WriteFile("post.csv", "id\n1\n2\n");
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER)", "CREATE TABLE log (n INTEGER)");

        await ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.PostSql = "INSERT INTO log SELECT count(*) FROM t; UPDATE t SET id = id * 10;"));
        Assert.Equal(2L, await db.Session.ExecuteScalarAsync<long>("SELECT n FROM log"));
        Assert.Equal(30L, await db.Session.ExecuteScalarAsync<long>("SELECT sum(id) FROM t"));

        var ex = await Assert.ThrowsAsync<ImportException>(() => ImportEngine.RunAsync(db.Session, Job(path, "t", j => j.PostSql = "INSERT INTO nope VALUES (1)")));
        Assert.Contains("post-SQL", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t")); // The second import was rolled back.
    }

    [Fact]
    public async Task Imports_excel_and_json_sources()
    {
        await using var db = await TestDatabase.CreateAsync();

        await ImportEngine.RunAsync(db.Session, Job(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reps.xls"), "reps"));
        await ImportEngine.RunAsync(db.Session, Job(WriteFile("j.json", """[{"a": 1, "b": "x"}, {"a": 2, "b": null}]"""), "j"));

        var reps = await RowsAsync(db, "SELECT \"Rep ID\", Name, Hired, Quota FROM reps ORDER BY 1");
        Assert.Equal([101L, "Ann", "2024-01-15", 1500.5], reps[0]);
        Assert.Equal([[1L, "x"], [2L, null]], await RowsAsync(db, "SELECT a, b FROM j ORDER BY a"));
    }

    [Fact]
    public async Task Bulk_inserts_are_logged_once_with_a_count()
    {
        var path = WriteFile("log.csv", "id\n" + string.Join("\n", Enumerable.Range(1, 50)));
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (id INTEGER)");
        db.Log.Clear();

        await ImportEngine.RunAsync(db.Session, Job(path, "t"));

        var inserts = db.Log.GetEntries().Where(e => e.Sql.StartsWith("INSERT", StringComparison.Ordinal)).ToList();
        Assert.Equal("INSERT INTO \"t\" (\"id\") VALUES (@p0)   -- executed 50 times", Assert.Single(inserts).Sql);
    }

    [Fact]
    public async Task Imports_100000_csv_rows_in_under_ten_seconds()
    {
        var builder = new StringBuilder("id,name,amount,day\n");
        for (var i = 1; i <= 100_000; i++)
        {
            builder.Append(i).Append(",Customer ").Append(i).Append(',').Append(i * 1.25).Append(",2026-01-").Append((i % 28) + 1).Append('\n');
        }

        var path = WriteFile("big.csv", builder.ToString());
        await using var db = await TestDatabase.CreateAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await ImportEngine.RunAsync(db.Session, Job(path, "big"));
        stopwatch.Stop();

        Assert.Equal(100_000, result.Inserted);
        Assert.Equal(100_000L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM big"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Import took {stopwatch.Elapsed}.");
    }
}
