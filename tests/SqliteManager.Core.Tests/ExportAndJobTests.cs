using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Transfer;

namespace SqliteManager.Core.Tests;

public sealed class ExportAndJobTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 14, 5, 9);
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

    private static readonly string[] Payouts =
    [
        "CREATE TABLE payouts (rep TEXT, period TEXT, amount REAL, note TEXT, data BLOB)",
        "INSERT INTO payouts VALUES ('Ann', '2026-10', 100.5, 'a, \"quoted\"', x'CAFE'), ('Bob', '2026-10', -3, NULL, NULL), ('Cy', '2026-09', 7, 'line1\nline2', NULL)",
    ];

    private ExportJob Export(ExportFormat format, Action<ExportJob>? configure = null)
    {
        var job = new ExportJob { Name = "Payout Export", Sql = "SELECT rep, amount, note, data FROM payouts WHERE period = @Period ORDER BY rep", Format = format, OutputPath = Path.Combine(_folder, "out_{date}" + ExportEngine.Extension(format)), IfFileExists = IfFileExists.Overwrite };
        configure?.Invoke(job);
        return job;
    }

    private static Dictionary<string, object?> Period => new() { ["@Period"] = "2026-10" };

    private static Task<ExportResult> RunExport(TestDatabase db, ExportJob job, Func<string, bool?>? confirm = null) =>
        db.Session.RunExclusiveAsync(r => ExportEngine.Run(r, job, Period, confirm, null, CancellationToken.None, Now));

    // ---- Export ---------------------------------------------------------------------------

    [Fact]
    public void ResolvePath_replaces_tokens()
    {
        Assert.Equal(@"C:\x\Pay_Out_2026-10-31_2026-10-31_140509.csv", ExportEngine.ResolvePath(@"C:\x\{JobName}_{date}_{datetime}.csv", "Pay/Out", Now));
    }

    [Fact]
    public async Task Csv_export_quotes_as_needed_and_writes_a_bom()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);

        var result = await RunExport(db, Export(ExportFormat.Csv));

        Assert.Equal(Path.Combine(_folder, "out_2026-10-31.csv"), result.FilePath);
        Assert.Equal(2, result.Rows);
        var bytes = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("rep,amount,note,data\r\nAnn,100.5,\"a, \"\"quoted\"\"\",CAFE\r\nBob,-3,,\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Theory]
    [InlineData(CsvQuoting.Always, "utf-8-nobom", "\"rep\";\"amount\"\r\n\"Ann\";\"100.5\"\r\n")]
    [InlineData(CsvQuoting.Never, "utf-8-nobom", "rep;amount\r\nAnn;100.5\r\n")]
    public async Task Csv_quoting_delimiter_and_encoding_options(CsvQuoting quoting, string encoding, string expected)
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var job = Export(ExportFormat.Csv, j =>
        {
            j.Sql = "SELECT rep, amount FROM payouts WHERE rep = 'Ann'";
            j.Options = new ExportOptions { Delimiter = ";", Quoting = quoting, Encoding = encoding };
        });

        var result = await RunExport(db, job);

        Assert.Equal(expected, await File.ReadAllTextAsync(result.FilePath));
    }

    [Fact]
    public async Task Tsv_export_without_header()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);

        var result = await RunExport(db, Export(ExportFormat.Tsv, j => j.Options = new ExportOptions { IncludeHeader = false, Encoding = "utf-8-nobom" }));

        Assert.StartsWith("Ann\t100.5\t", await File.ReadAllTextAsync(result.FilePath));
    }

    [Fact]
    public async Task Xlsx_export_with_sheet_header_freeze_and_autofit()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var job = Export(ExportFormat.Xlsx, j => j.Options = new ExportOptions { SheetName = "Pay/outs: Oct", IncludeHeader = true, FreezeHeader = true, AutoFit = true });

        var result = await RunExport(db, job);

        using var wb = new XLWorkbook(result.FilePath);
        var ws = wb.Worksheet(1);
        Assert.Equal("Pay_outs_ Oct", ws.Name);
        Assert.Equal("rep", ws.Cell(1, 1).GetText());
        Assert.True(ws.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(100.5, ws.Cell(2, 2).GetDouble());
        Assert.Equal("CAFE", ws.Cell(2, 4).GetText());
        Assert.True(ws.Cell(3, 3).IsEmpty());
        Assert.Equal(1, ws.SheetView.SplitRow);
    }

    [Fact]
    public async Task Json_export_writes_typed_values_and_base64_blobs()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);

        var result = await RunExport(db, Export(ExportFormat.Json));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.FilePath));
        var first = doc.RootElement[0];
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal(100.5, first.GetProperty("amount").GetDouble());
        Assert.Equal(new byte[] { 0xCA, 0xFE }, first.GetProperty("data").GetBytesFromBase64());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Json_export_of_no_rows_is_an_empty_array()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);

        var result = await RunExport(db, Export(ExportFormat.Json, j => j.Sql = "SELECT * FROM payouts WHERE 0"));

        Assert.Equal("[]", (await File.ReadAllTextAsync(result.FilePath)).Trim());
    }

    [Fact]
    public async Task Sql_export_can_be_run_again()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var result = await RunExport(db, Export(ExportFormat.Sql, j =>
        {
            j.Sql = "SELECT * FROM payouts";
            j.Options = new ExportOptions { TableName = "copy" };
        }));
        var script = await File.ReadAllTextAsync(result.FilePath);

        await db.Session.ExecuteNonQueryAsync("CREATE TABLE copy AS SELECT * FROM payouts WHERE 0");
        await new Query.QueryExecutor(db.Session).ExecuteAsync(script, new Query.QueryRunOptions(10));

        Assert.Contains("X'CAFE'", script, StringComparison.Ordinal);
        Assert.Equal(
            (await db.Session.QueryRowsAsync("SELECT * FROM payouts ORDER BY rep")).Rows,
            (await db.Session.QueryRowsAsync("SELECT * FROM copy ORDER BY rep")).Rows);
    }

    [Fact]
    public async Task Table_export_and_csv_round_trip_through_import()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var export = Export(ExportFormat.Csv, j =>
        {
            j.Sql = null;
            j.Table = "payouts";
        });
        var file = await RunExport(db, export);

        await ImportEngine.RunAsync(db.Session, new ImportJob
        {
            SourcePath = file.FilePath,
            TargetTable = "payouts2",
            CreateTableIfMissing = true,
            ColumnMappings = [new() { Source = "rep", Target = "rep" }, new() { Source = "note", Target = "note" }],
        });

        Assert.Equal(
            (await db.Session.QueryRowsAsync("SELECT rep, coalesce(note, '') FROM payouts ORDER BY rep")).Rows,
            (await db.Session.QueryRowsAsync("SELECT rep, note FROM payouts2 ORDER BY rep")).Rows);
    }

    [Fact]
    public async Task If_file_exists_fail_prompt_and_overwrite()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var path = Path.Combine(_folder, "out_2026-10-31.csv");
        await File.WriteAllTextAsync(path, "keep me");

        await Assert.ThrowsAsync<ExportException>(() => RunExport(db, Export(ExportFormat.Csv, j => j.IfFileExists = IfFileExists.Fail)));
        await Assert.ThrowsAsync<ExportException>(() => RunExport(db, Export(ExportFormat.Csv, j => j.IfFileExists = IfFileExists.Prompt))); // Cannot ask.
        await Assert.ThrowsAsync<ExportException>(() => RunExport(db, Export(ExportFormat.Csv, j => j.IfFileExists = IfFileExists.Prompt), _ => false));
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));

        await RunExport(db, Export(ExportFormat.Csv, j => j.IfFileExists = IfFileExists.Prompt), p => p == path);
        Assert.StartsWith("rep,", (await File.ReadAllTextAsync(path)).TrimStart('\uFEFF'));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Failed_export_leaves_the_old_file_and_no_temp_file()
    {
        await using var db = await TestDatabase.CreateAsync(Payouts);
        var path = Path.Combine(_folder, "out_2026-10-31.csv");
        await File.WriteAllTextAsync(path, "keep me");

        await Assert.ThrowsAnyAsync<Exception>(() => RunExport(db, Export(ExportFormat.Csv, j => j.Sql = "SELECT * FROM missing")));

        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    // ---- Jobs -----------------------------------------------------------------------------

    private sealed class TestHost : IJobHost
    {
        public List<string> Messages { get; } = [];

        public bool? Overwrite { get; set; }

        public void Report(string message)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
        }

        public bool? ConfirmOverwrite(string path) => Overwrite;
    }

    private async Task<(string DbPath, JobFile Jobs, RunHistoryStore History, TestHost Host, JobRunner Runner)> SetupJobsAsync(params Job[] jobs)
    {
        var dbPath = Path.Combine(_folder, "comp.db");
        await using (var manager = new Data.DatabaseManager(new SqlLog()))
        {
            var s = await manager.CreateAsync(dbPath);
            await s.ExecuteNonQueryAsync("CREATE TABLE payouts (rep TEXT, period TEXT, amount REAL)");
            await s.ExecuteNonQueryAsync("CREATE TABLE payout_staging (rep TEXT, period TEXT, amount REAL, exception INTEGER DEFAULT 0)");
            await s.ExecuteNonQueryAsync("CREATE TABLE reps (rep_id INTEGER PRIMARY KEY, rep_name TEXT)");
            await s.ExecuteNonQueryAsync("INSERT INTO payout_staging (rep, period, amount) VALUES ('Ann', '2026-10', 10), ('Bob', '2026-10', -5), ('Old', '2026-09', 1)");
        }

        foreach (var job in jobs)
        {
            job.DatabasePath = dbPath;
        }

        var file = new JobFile { Jobs = [.. jobs] };
        var history = new RunHistoryStore(Path.Combine(_folder, "history.jsonl"));
        var host = new TestHost();
        return (dbPath, file, history, host, new JobRunner(file, history, new SqlLog(), host));
    }

    private static async Task<long> ScalarAsync(string dbPath, string sql)
    {
        await using var manager = new Data.DatabaseManager(new SqlLog());
        var s = await manager.OpenAsync(dbPath);
        return await s.ExecuteScalarAsync<long>(sql);
    }

    private ScriptJob MonthEnd(StepErrorMode flagOnError = StepErrorMode.Continue) => new()
    {
        Name = "Month-End",
        Parameters = [new JobParameter { Name = "@Period", Prompt = "Period (YYYY-MM)", Default = "" }],
        Steps =
        [
            new RunJobStep { Name = "Load roster", Job = "Load Reps" },
            new SqlStep { Name = "Flag", Sql = "UPDATE payout_staging SET exception = 1 WHERE period = @Period AND amount < 0;", OnError = flagOnError },
            new SqlStep { Name = "Publish", Sql = "INSERT INTO payouts SELECT rep, period, amount FROM payout_staging WHERE period = @Period AND exception = 0;" },
            new RunJobStep { Name = "Export", Job = "Export Payouts" },
        ],
    };

    private ImportJob LoadReps(string csv)
    {
        var path = Path.Combine(_folder, "roster.csv");
        File.WriteAllText(path, csv);
        return new ImportJob
        {
            Name = "Load Reps",
            SourcePath = path,
            TargetTable = "reps",
            LoadMode = LoadMode.Upsert,
            KeyColumns = ["rep_id"],
            ColumnMappings = [new() { Source = "Rep ID", Target = "rep_id" }, new() { Source = "Name", Target = "rep_name" }],
        };
    }

    private ExportJob ExportPayouts() => new()
    {
        Name = "Export Payouts",
        Sql = "SELECT * FROM payouts WHERE period = @Period",
        Format = ExportFormat.Csv,
        OutputPath = Path.Combine(_folder, "payouts_{jobname}.csv"),
        IfFileExists = IfFileExists.Overwrite,
        Parameters = [new JobParameter { Name = "@Period", Default = "" }],
    };

    [Fact]
    public async Task Month_end_script_succeeds_and_records_history()
    {
        var script = MonthEnd();
        var (dbPath, _, history, host, runner) = await SetupJobsAsync(script, LoadReps("Rep ID,Name\n1,Ann\n2,Bob\n"), ExportPayouts());

        var result = await runner.RunAsync(script, new Dictionary<string, string> { ["Period"] = "2026-10" });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2L, await ScalarAsync(dbPath, "SELECT count(*) FROM reps"));
        Assert.Equal(1L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts"));
        Assert.Equal("rep,period,amount\r\nAnn,2026-10,10\r\n", (await File.ReadAllTextAsync(Path.Combine(_folder, "payouts_Export Payouts.csv"))).TrimStart('\uFEFF'));

        var records = await history.LoadAsync();
        Assert.Equal("Month-End", records[0].Job);
        Assert.Equal(RunRecord.Succeeded, records[0].Status);
        Assert.Contains(records, r => r.Job == "Load Reps" && r.Parent == "Month-End");
        Assert.Contains(records, r => r.Job == "Month-End › Publish" && r.Rows == 1);
        Assert.Contains(host.Messages, m => m.StartsWith("Month-End: done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failing_stop_step_rolls_back_every_step_including_nested_imports()
    {
        var script = MonthEnd();
        script.Steps.Add(new SqlStep { Name = "Broken", Sql = "INSERT INTO nope VALUES (1)" });
        var (dbPath, _, history, _, runner) = await SetupJobsAsync(script, LoadReps("Rep ID,Name\n1,Ann\n"), ExportPayouts());

        var result = await runner.RunAsync(script, new Dictionary<string, string> { ["@Period"] = "2026-10" });

        Assert.False(result.Succeeded);
        Assert.Contains("Broken", result.Error, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(dbPath, "SELECT count(*) FROM reps"));
        Assert.Equal(0L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts"));
        Assert.Equal(0L, await ScalarAsync(dbPath, "SELECT count(*) FROM payout_staging WHERE exception = 1"));
        Assert.Equal(RunRecord.Failed, (await history.LoadAsync())[0].Status);
    }

    [Fact]
    public async Task Continue_step_failure_does_not_stop_the_job()
    {
        var script = MonthEnd();
        ((SqlStep)script.Steps[1]).Sql = "UPDATE nope SET x = 1";
        var (dbPath, _, _, host, runner) = await SetupJobsAsync(script, LoadReps("Rep ID,Name\n1,Ann\n"), ExportPayouts());

        var result = await runner.RunAsync(script, new Dictionary<string, string> { ["@Period"] = "2026-10" });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts")); // Bob was not flagged.
        Assert.Contains(host.Messages, m => m.Contains("failed, continuing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transaction_mode_none_keeps_steps_that_finished()
    {
        var script = new ScriptJob
        {
            Name = "Steps",
            TransactionMode = TransactionMode.None,
            Steps = [new SqlStep { Name = "One", Sql = "INSERT INTO payouts VALUES ('x', 'p', 1)" }, new SqlStep { Name = "Two", Sql = "INSERT INTO nope VALUES (1)" }],
        };
        var (dbPath, _, _, _, runner) = await SetupJobsAsync(script);

        var result = await runner.RunAsync(script, new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.Equal(1L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts"));
    }

    [Fact]
    public async Task Missing_parameters_fail_and_defaults_are_used()
    {
        var export = ExportPayouts();
        var (_, _, _, _, runner) = await SetupJobsAsync(export);

        var missing = await runner.RunAsync(export, new Dictionary<string, string>());
        Assert.False(missing.Succeeded);
        Assert.Contains("@Period", missing.Error, StringComparison.Ordinal);

        export.Parameters![0].Default = "2026-09";
        var withDefault = await runner.RunAsync(export, new Dictionary<string, string>());
        Assert.True(withDefault.Succeeded, withDefault.Error);
        Assert.Equal(["@Period"], runner.ParametersOf(MonthEnd()).Select(p => p.Name));
    }

    [Fact]
    public async Task Sql_file_steps_and_timeouts()
    {
        var sqlFile = Path.Combine(_folder, "calc.sql");
        await File.WriteAllTextAsync(sqlFile, "INSERT INTO payouts VALUES ('f', 'p', 2);\nINSERT INTO payouts VALUES ('g', 'p', 3);");
        var script = new ScriptJob
        {
            Name = "Files",
            Steps =
            [
                new SqlStep { Name = "From file", SqlFile = sqlFile },
                new SqlStep { Name = "Slow", Sql = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) FROM n", TimeoutSeconds = 1 },
            ],
        };
        var (dbPath, _, _, _, runner) = await SetupJobsAsync(script);

        var result = await runner.RunAsync(script, new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.Contains("longer than 1 second", result.Error, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts"));
    }

    [Fact]
    public async Task Jobs_that_call_themselves_are_refused()
    {
        var a = new ScriptJob { Name = "A", Steps = [new RunJobStep { Name = "call B", Job = "B" }] };
        var b = new ScriptJob { Name = "B", Steps = [new RunJobStep { Name = "call A", Job = "A" }] };
        var (_, _, _, _, runner) = await SetupJobsAsync(a, b);

        var result = await runner.RunAsync(a, new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.Contains("calls itself (A → B → A)", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_database_fails_cleanly()
    {
        var job = new ScriptJob { Name = "X", DatabasePath = Path.Combine(_folder, "nope.db"), Steps = [new SqlStep { Name = "s", Sql = "SELECT 1" }] };
        var runner = new JobRunner(new JobFile { Jobs = [job] }, null, new SqlLog(), new TestHost());

        var result = await runner.RunAsync(job, new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(job.DatabasePath));
    }

    // ---- Command line ---------------------------------------------------------------------

    [Fact]
    public void Parse_reads_jobs_parameters_and_files()
    {
        var parsed = CommandLine.Parse(["run", "--job", "Monthly Payout Export", "--param", "@Period=2026-10", "--param", "Region=EU=x", "--job", "B", "--jobs-file", "j.json"]);

        Assert.Equal("run", parsed.Command);
        Assert.Equal(["Monthly Payout Export", "B"], parsed.Jobs);
        Assert.Equal("2026-10", parsed.Parameters["@Period"]);
        Assert.Equal("EU=x", parsed.Parameters["@Region"]);
        Assert.Equal("j.json", parsed.JobsFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("go")]
    [InlineData("run")]
    [InlineData("run|--job")]
    [InlineData("run|--job|a|--param|nope")]
    [InlineData("list|--what")]
    public void Parse_rejects_bad_arguments(string args)
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split('|', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public async Task Cli_list_run_and_exit_codes()
    {
        var script = MonthEnd();
        var (dbPath, jobs, _, _, _) = await SetupJobsAsync(script, LoadReps("Rep ID,Name\n1,Ann\n"), ExportPayouts());
        var jobsFile = Path.Combine(_folder, "jobs.json");
        await new JobStore(jobsFile).SaveAsync(jobs);
        var historyFile = Path.Combine(_folder, "cli-history.jsonl");

        async Task<(int Code, string Out, string Err)> Cli(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await CommandLine.RunAsync([.. args, "--jobs-file", jobsFile, "--history-file", historyFile], output, error);
            return (code, output.ToString(), error.ToString());
        }

        var list = await Cli("list");
        Assert.Equal(0, list.Code);
        Assert.Contains("Month-End", list.Out, StringComparison.Ordinal);
        Assert.Contains("script", list.Out, StringComparison.Ordinal);

        var ok = await Cli("run", "--job", "Load Reps", "--job", "Month-End", "--param", "@Period=2026-10");
        Assert.Equal(0, ok.Code);
        Assert.Contains("Month-End: done", ok.Out, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync(dbPath, "SELECT count(*) FROM payouts"));

        var missingParam = await Cli("run", "--job", "Month-End");
        Assert.Equal(1, missingParam.Code);
        Assert.Contains("@Period", missingParam.Err, StringComparison.Ordinal);

        var unknown = await Cli("run", "--job", "Nope");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("No job is named \"Nope\"", unknown.Err, StringComparison.Ordinal);

        Assert.True((await new RunHistoryStore(historyFile).LoadAsync()).Count >= 3);
        Assert.Equal(0, (await Cli("--help")).Code);
    }

    [Fact]
    public async Task Cli_prompt_export_over_existing_file_fails()
    {
        var export = ExportPayouts();
        export.Parameters![0].Default = "2026-10";
        export.IfFileExists = IfFileExists.Prompt;
        var (_, jobs, _, _, _) = await SetupJobsAsync(export);
        var jobsFile = Path.Combine(_folder, "jobs.json");
        await new JobStore(jobsFile).SaveAsync(jobs);
        await File.WriteAllTextAsync(Path.Combine(_folder, "payouts_Export Payouts.csv"), "keep");

        var error = new StringWriter();
        var code = await CommandLine.RunAsync(["run", "--job", "Export Payouts", "--jobs-file", jobsFile, "--history-file", Path.Combine(_folder, "h.jsonl")], new StringWriter(), error);

        Assert.Equal(1, code);
        Assert.Contains("treats as \"fail\"", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_folder, "payouts_Export Payouts.csv")));
    }
}
