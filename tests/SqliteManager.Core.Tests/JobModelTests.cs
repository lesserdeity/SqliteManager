using System.Text.Json;
using System.Text.Json.Nodes;
using SqliteManager.Core.Jobs;

namespace SqliteManager.Core.Tests;

public sealed class JobModelTests : IDisposable
{
    /// <summary>The job file example from docs/SPEC.md section 4.8.</summary>
    public const string SpecExample = """
        {
          "version": 1,
          "jobs": [
            {
              "id": "6f1c2e4a-0000-0000-0000-000000000001",
              "name": "Monthly Payout Export",
              "type": "export",
              "databasePath": "C:\\Data\\comp.db",
              "sql": "SELECT * FROM payouts WHERE period = @Period",
              "parameters": [ { "name": "@Period", "prompt": "Period (YYYY-MM)", "default": "" } ],
              "format": "xlsx",
              "outputPath": "C:\\Exports\\payouts_{date}.xlsx",
              "options": { "sheetName": "Payouts", "includeHeader": true, "autoFit": true },
              "ifFileExists": "overwrite"
            },
            {
              "id": "6f1c2e4a-0000-0000-0000-000000000002",
              "name": "Load Rep Roster",
              "type": "import",
              "databasePath": "C:\\Data\\comp.db",
              "sourcePath": "C:\\Inbound\\roster.csv",
              "sourceFormat": "csv",
              "sourceOptions": { "delimiter": ",", "hasHeader": true, "encoding": "utf-8" },
              "targetTable": "reps",
              "loadMode": "upsert",
              "keyColumns": [ "rep_id" ],
              "createTableIfMissing": false,
              "columnMappings": [
                { "source": "Rep ID", "target": "rep_id" },
                { "source": "Name", "target": "rep_name" }
              ],
              "onError": "rollback",
              "preSql": "",
              "postSql": ""
            },
            {
              "id": "6f1c2e4a-0000-0000-0000-000000000003",
              "name": "Month-End Payout Calculation",
              "type": "script",
              "databasePath": "C:\\Data\\comp.db",
              "transactionMode": "single",
              "parameters": [
                { "name": "@Period", "prompt": "Period (YYYY-MM)", "default": "" }
              ],
              "steps": [
                { "name": "Load latest roster", "type": "runJob", "job": "Load Rep Roster" },
                { "name": "Clear staging", "type": "sql", "sql": "DELETE FROM payout_staging WHERE period = @Period;", "onError": "stop" },
                { "name": "Calculate payouts", "type": "sql", "sqlFile": "C:\\Scripts\\calc_payouts.sql", "onError": "stop", "timeoutSeconds": 300 },
                { "name": "Flag exceptions", "type": "sql", "sql": "UPDATE payout_staging SET exception = 1 WHERE period = @Period AND amount < 0;", "onError": "continue" },
                { "name": "Publish to payouts", "type": "sql", "sql": "INSERT INTO payouts SELECT * FROM payout_staging WHERE period = @Period AND exception = 0;", "onError": "stop" },
                { "name": "Export results", "type": "runJob", "job": "Monthly Payout Export" }
              ]
            }
          ]
        }
        """;

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

    [Fact]
    public void Spec_example_loads_with_the_right_types()
    {
        var file = JsonSerializer.Deserialize<JobFile>(SpecExample, JobJson.Options)!;

        Assert.Equal(1, file.Version);
        var export = Assert.IsType<ExportJob>(file.Jobs[0]);
        Assert.Equal((ExportFormat.Xlsx, IfFileExists.Overwrite, "Payouts", true), (export.Format, export.IfFileExists, export.Options!.SheetName, export.Options.AutoFit));
        Assert.Equal("@Period", export.Parameters![0].Name);

        var import = Assert.IsType<ImportJob>(file.Jobs[1]);
        Assert.Equal((LoadMode.Upsert, ErrorMode.Rollback, SourceFormat.Csv, false), (import.LoadMode, import.OnError, import.SourceFormat, import.CreateTableIfMissing));
        Assert.Equal(["rep_id"], import.KeyColumns);
        Assert.Equal("Rep ID", import.ColumnMappings![0].Source);

        var script = Assert.IsType<ScriptJob>(file.Jobs[2]);
        Assert.Equal(TransactionMode.Single, script.TransactionMode);
        Assert.Equal("Load Rep Roster", Assert.IsType<RunJobStep>(script.Steps[0]).Job);
        var calc = Assert.IsType<SqlStep>(script.Steps[2]);
        Assert.Equal(("C:\\Scripts\\calc_payouts.sql", 300, StepErrorMode.Stop), (calc.SqlFile, calc.TimeoutSeconds, calc.OnError));
        Assert.Equal(StepErrorMode.Continue, Assert.IsType<SqlStep>(script.Steps[3]).OnError);
        Assert.Equal("Monthly Payout Export", file.Find("monthly payout export")!.Name);
    }

    [Fact]
    public void Spec_example_saves_back_unchanged()
    {
        var file = JsonSerializer.Deserialize<JobFile>(SpecExample, JobJson.Options)!;

        var saved = JsonSerializer.Serialize(file, JobJson.Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SpecExample), JsonNode.Parse(saved)), saved);
    }

    [Fact]
    public async Task Store_round_trip_and_missing_file()
    {
        var store = new JobStore(Path.Combine(_folder, "sub", "jobs.json"));
        Assert.Empty((await store.LoadAsync()).Jobs);

        var file = JsonSerializer.Deserialize<JobFile>(SpecExample, JobJson.Options)!;
        await store.SaveAsync(file);
        var loaded = await store.LoadAsync();

        Assert.Equal(3, loaded.Jobs.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SpecExample), JsonNode.Parse(await File.ReadAllTextAsync(store.FilePath))));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Fact]
    public async Task Store_refuses_duplicate_names()
    {
        var store = new JobStore(Path.Combine(_folder, "jobs.json"));
        var file = new JobFile { Jobs = [new ExportJob { Name = "A" }, new ImportJob { Name = "a" }] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(file));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Damaged_store_raises_json_exception()
    {
        var store = new JobStore(Path.Combine(_folder, "jobs.json"));
        await File.WriteAllTextAsync(store.FilePath, "{ not json");

        await Assert.ThrowsAnyAsync<JsonException>(() => store.LoadAsync());
    }

    [Fact]
    public void Duplicate_copies_everything_with_a_new_id()
    {
        var file = JsonSerializer.Deserialize<JobFile>(SpecExample, JobJson.Options)!;
        var original = (ScriptJob)file.Jobs[2];

        var copy = (ScriptJob)original.Duplicate("Copy");
        ((SqlStep)copy.Steps[1]).Sql = "changed";

        Assert.Equal("Copy", copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(6, copy.Steps.Count);
        Assert.NotEqual("changed", ((SqlStep)original.Steps[1]).Sql);
    }

    [Fact]
    public async Task History_appends_lines_and_loads_newest_first()
    {
        var history = new RunHistoryStore(Path.Combine(_folder, "h", "history.jsonl"));
        var t = new DateTime(2026, 11, 12, 9, 0, 0);

        history.Append(new RunRecord("A", t, t.AddSeconds(1), 10, RunRecord.Succeeded, null));
        history.Append(new RunRecord("B", t, t.AddSeconds(2), 0, RunRecord.Failed, "boom", "A"));
        await File.AppendAllTextAsync(history.FilePath, "garbage\n");
        var records = await history.LoadAsync();

        Assert.Equal(["B", "A"], records.Select(r => r.Job));
        Assert.Equal(("boom", "A"), (records[0].Error, records[0].Parent));
        var lines = await File.ReadAllLinesAsync(history.FilePath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"job\":\"A\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"status\":\"succeeded\"", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_survives_concurrent_writers()
    {
        var history = new RunHistoryStore(Path.Combine(_folder, "history.jsonl"));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            history.Append(new RunRecord($"J{i}", DateTime.Now, DateTime.Now, i, RunRecord.Succeeded, null)))));

        Assert.Equal(40, (await history.LoadAsync()).Count);
    }
}
