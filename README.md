# SqliteManager

A Windows desktop manager for SQLite databases, built with .NET 10 and WinForms.
It has an Office-style ribbon, a SQL editor, visual table and query designers,
import and export, and saved jobs that you can run from the app or from the command line.

## Features

- **Databases**: create, open, or open read-only. Work with several databases at once, attach other files, and reopen recent files.
- **Object explorer**: tables, views, indexes, and triggers. Tables expand to show columns, keys, and indexes. Script as CREATE, INSERT, or DROP. Rename and drop objects (drops ask first).
- **Data grid**: browse large tables quickly with a virtual grid. Filter, sort, and edit cells. Insert and delete rows, set NULL, and view or edit BLOBs as hex, text, or images. Changes are written together in one transaction.
- **Query editor**: syntax highlighting, autocomplete, and folding. Run all, the selection, or the statement at the caret, and cancel a running query. Each result set opens in its own tab. Also includes query history, Explain Plan, and opening and saving `.sql` files.
- **Table designer**: edit columns, indexes, foreign keys, and constraints. The SQL is previewed before you apply it. Changes that SQLite's `ALTER TABLE` cannot make rebuild the table and keep the data, triggers, and views.
- **Query designer**: a visual designer in the style of Microsoft Access. Drag tables onto the canvas, draw joins, tick columns, and type criteria, totals, and sorting. Then run the query or send its SQL to a query tab.
- **Import and export**: import CSV, TSV, Excel (`.xlsx` and `.xls`), and JSON. Export to CSV, TSV, `.xlsx`, JSON, or a SQL script.
- **Saved jobs**: imports, exports, and SQL scripts with parameters. Each run is added to the run history.
- **Maintenance**: VACUUM, ANALYZE, integrity and foreign key checks, a PRAGMA editor, and online backup.
- **SQL log**: every statement the app runs is shown in the SQL Log pane.
- **Light and dark themes**.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and run

```
dotnet build SqliteManager.sln
dotnet run --project src/SqliteManager.App
```

Run the tests:

```
dotnet test SqliteManager.sln
```

## Command-line job runner

`SqliteManager.Cli` runs jobs that were saved in the app, so you can schedule them with Windows Task Scheduler.

```
SqliteManager.Cli list
SqliteManager.Cli run --job "Monthly Payout Export" --param @Period=2026-10
SqliteManager.Cli run --job "Load Rep Roster" --job "Monthly Payout Export"
```

| Option | Meaning |
|---|---|
| `--job <name>` | A job to run. Repeat to run several jobs in order. |
| `--param @Name=value` | A parameter value. Repeat for more parameters. |
| `--jobs-file <path>` | Use this jobs file instead of the default one. |
| `--history-file <path>` | Write run history to this file instead of the default one. |

The exit code is 0 when every job succeeds and 1 when a job fails.

## Where data is stored

Settings, jobs, and history are kept in `%APPDATA%\SqliteManager\`:

| File | Contents |
|---|---|
| `settings.json` | Options, recent files, window layout, and theme |
| `jobs.json` | Saved jobs |
| `history.jsonl` | Job run history |
| `query-history.jsonl` | The last 1,000 queries run in the editor |

## Project layout

```
SqliteManager.sln
  src/SqliteManager.Core/          Database, schema, DDL, import/export, and job logic
  src/SqliteManager.App/           WinForms user interface
  src/SqliteManager.Cli/           Command-line job runner
  tests/SqliteManager.Core.Tests/  xUnit tests for Core
docs/SPEC.md                       Full specification
```

The app and the CLI share the job engine in `Core`. All SQL lives in `Core`, values are always passed as parameters, and every identifier is quoted.

## Built with

[Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite),
[Scintilla5.NET](https://www.nuget.org/packages/Scintilla5.NET),
[ClosedXML](https://www.nuget.org/packages/ClosedXML),
[ExcelDataReader](https://www.nuget.org/packages/ExcelDataReader),
[CsvHelper](https://www.nuget.org/packages/CsvHelper), and
[xUnit](https://xunit.net/).

This project was developed with [Claude Code](https://claude.com/claude-code).

## License

[MIT](LICENSE)
