# SQLite Manager — Project Specification

## 1. Goal

Build a Windows desktop database manager for SQLite in C#.
The application combines the main features of DB Browser for SQLite and SQL Server Management Studio (SSMS).
It also adds an Access-style query designer and saved import/export jobs.
Use WinForms. The UI should resemble a Microsoft Office application.

## 2. Technology

| Area | Choice |
|---|---|
| Runtime | .NET 10 (LTS), `net10.0-windows`. If a library does not support it, use .NET 8 (LTS). |
| UI | Windows Forms. Use `DataGridView` for all data grids. |
| SQLite driver | `Microsoft.Data.Sqlite` with the default `SQLitePCLRaw` bundle |
| SQL editor | `Scintilla5.NET` (syntax highlighting, autocomplete, line numbers, folding) |
| Excel | `ClosedXML` (read and write .xlsx); `ExcelDataReader` + `ExcelDataReader.DataSet` (read legacy .xls) |
| CSV | `CsvHelper` |
| JSON | `System.Text.Json` |
| Tests | xUnit |
| Query designer canvas | Custom-drawn WinForms control (GDI+). Do not add a diagram library. |

## 3. Architecture

Use this solution structure:

```
SqliteManager.sln
  src/SqliteManager.Core/        Class library. All database, schema, DDL, import/export, and job logic.
  src/SqliteManager.App/         WinForms application. UI only.
  src/SqliteManager.Cli/         Console application. Runs saved jobs.
  tests/SqliteManager.Core.Tests/
```

Rules:

- Keep all SQL and file logic in `Core`. Forms must call `Core` services. Forms must not contain SQL.
- `Core` must not reference WinForms.
- `App` and `Cli` both use the same `Core` job engine.
- Run long operations (queries, imports, exports, VACUUM) asynchronously. Do not freeze the UI.
- Use parameterized queries for all values.
- Quote all identifiers with double quotes. Escape embedded double quotes.
- Write every SQL statement that the application issues to an in-app SQL log.

## 4. Features

### 4.1 Connections and files

- Create a new database file. Open an existing file. Open in read-only mode.
- Open more than one database at the same time.
- Show a recent-files list.
- Support `ATTACH DATABASE` for work across files.

### 4.2 Object explorer

- Tree view: database > Tables, Views, Indexes, Triggers.
- Show columns, keys, and indexes under each table.
- Context menu actions: Open data, Design, New query, Script as (CREATE, INSERT, DROP), Rename, Drop, Refresh.

### 4.3 Data browsing and CRUD

- Show table data in a `DataGridView`. Use virtual mode or paging. A table with 1,000,000 rows must open in less than 2 seconds.
- Insert, edit, and delete rows in the grid.
- Keep edits as pending changes. Show pending rows with a visual marker.
- Provide **Write Changes** and **Revert Changes**. Write all pending changes in one transaction.
- Show NULL differently from an empty string. Let the user set a cell to NULL.
- Filter per column (filter row above the grid). Sort by column.
- BLOB viewer: hex, text, and image preview. Load a BLOB from a file. Save a BLOB to a file.
- Copy selected cells to the clipboard as tab-separated text with headers.

### 4.4 Table designer (create, alter, drop)

- Create tables, alter tables, and drop tables through a visual designer.
- Grid of columns: name, type, primary key, autoincrement, not null, unique, default, check, collation.
- Tabs for indexes, foreign keys, and table options (`WITHOUT ROWID`, `STRICT`).
- Show the generated SQL in a preview pane before the user applies it.
- **Alter logic:** Use native `ALTER TABLE` when possible (rename table, rename column, add column, drop column). For all other changes, use the SQLite table rebuild procedure:
  1. `PRAGMA foreign_keys = OFF`
  2. `BEGIN TRANSACTION`
  3. Create the new table with a temporary name.
  4. Copy the data with `INSERT INTO new SELECT ... FROM old`.
  5. Drop the old table.
  6. Rename the new table to the old name.
  7. Recreate the indexes, triggers, and views that refer to the table.
  8. Run `PRAGMA foreign_key_check`. If it returns rows, roll back and show the errors.
  9. `COMMIT`
  10. `PRAGMA foreign_keys = ON`
- Unit-test the rebuild logic. Make sure no data is lost.
- Editors for views and triggers (SQL text with a preview).
- Ask for confirmation before any drop.

### 4.5 Query editor

- Multiple query tabs. Each tab connects to one open database.
- Syntax highlighting for SQLite SQL.
- Autocomplete for keywords, table names, column names (after `alias.` or `table.`), and functions. Read names from the live schema.
- Execute all (F5), execute selection, execute current statement (Ctrl+Enter).
- Show each result set in its own `DataGridView` tab.
- Messages pane: rows affected, execution time, errors with line numbers.
- Cancel a running query. Use `SQLitePCL.raw.sqlite3_interrupt` on the connection handle.
- Query history (searchable). Save and open `.sql` files.
- Show `EXPLAIN QUERY PLAN` output as a tree.
- Export a result grid with the export engine (section 4.7).

### 4.6 Query designer (Access style)

- Canvas: the user drags tables from the object explorer onto the canvas.
- Each table box lists its columns with check boxes.
- Draw joins automatically from foreign keys. Let the user drag a column to another column to make a join.
- Click a join line to set INNER, LEFT, or CROSS join.
- Criteria grid below the canvas: field, alias, table, show, sort, sort order, criteria, OR criteria, group by / aggregate (Totals mode).
- Generate SQL from the design. Generation is one-way: the user can send the SQL to a query tab and edit it there.
- Run the design and show results.
- Save and load designs as `.qdesign` JSON files.

### 4.7 Import and export engine

Formats:

- Import: CSV, TSV, .xlsx, .xls, JSON (array of objects).
- Export: CSV, TSV, .xlsx, JSON, SQL INSERT script.

Interactive wizards:

- Import wizard: choose file, set source options, preview the first 100 rows, map columns, choose target table and load mode, run.
- Export wizard: choose table or SQL, choose format and options, choose file, run.
- At the end of each wizard, offer **Save as job**.

Import options:

- CSV: delimiter, quote character, encoding, header row yes/no, rows to skip.
- Excel: sheet name, header row, start cell.
- Date and number formats for parsing.
- Column mapping: source column > target column, skip column, default value for unmapped target columns.
- Load modes:
  - `append` — insert rows.
  - `truncate` — delete all rows, then insert.
  - `replace` — drop the table and create it again from the source columns.
  - `upsert` — insert rows; update rows that match the key columns (`INSERT ... ON CONFLICT DO UPDATE`). If no unique index exists on the key columns, update matches first, then insert the rest.
- Create the target table if it does not exist (infer column types from the source).
- Error mode: `rollback` (stop at the first error, roll back all) or `skip` (skip bad rows, write them to an error file next to the source file).
- Pre-SQL and post-SQL that run inside the same transaction.
- Insert in batches inside one transaction. 100,000 CSV rows must import in less than 10 seconds.

Export options:

- CSV/TSV: delimiter, quote rules, header row, encoding.
- Excel: sheet name, header row, auto-fit columns, freeze header row.
- File name tokens: `{date}` (yyyy-MM-dd), `{datetime}` (yyyy-MM-dd_HHmmss), `{jobname}`.
- If the file exists: `overwrite`, `fail`, or `prompt` (interactive only).
- SQL parameters such as `@StartDate`. Prompt for values at run time. The CLI takes values as arguments.

### 4.8 Saved jobs

- Store jobs in `%APPDATA%\SqliteManager\jobs.json`.
- Each job stores its own database path, because jobs are app-level, not per database.
- Write the file atomically (write a temp file, then replace).
- Jobs window: list, create, edit, duplicate, delete, run selected, run all selected in order.
- Run history: append one JSON line per run to `%APPDATA%\SqliteManager\history.jsonl` (job, start, end, rows, status, error message). Show it in the Jobs window.
- Jobs should also be able to execute a series of SQL commands.

Job file example:

```json
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
        {
          "name": "Load latest roster",
          "type": "runJob",
          "job": "Load Rep Roster"
        },
        {
          "name": "Clear staging",
          "type": "sql",
          "sql": "DELETE FROM payout_staging WHERE period = @Period;",
          "onError": "stop"
        },
        {
          "name": "Calculate payouts",
          "type": "sql",
          "sqlFile": "C:\\Scripts\\calc_payouts.sql",
          "onError": "stop",
          "timeoutSeconds": 300
        },
        {
          "name": "Flag exceptions",
          "type": "sql",
          "sql": "UPDATE payout_staging SET exception = 1 WHERE period = @Period AND amount < 0;",
          "onError": "continue"
        },
        {
          "name": "Publish to payouts",
          "type": "sql",
          "sql": "INSERT INTO payouts SELECT * FROM payout_staging WHERE period = @Period AND exception = 0;",
          "onError": "stop"
        },
        {
          "name": "Export results",
          "type": "runJob",
          "job": "Monthly Payout Export"
        }
      ]
    }
  ]
}
```

### 4.9 Command-line runner

```
SqliteManager.Cli list
SqliteManager.Cli run --job "Monthly Payout Export" --param @Period=2026-10
SqliteManager.Cli run --job "Load Rep Roster" --job "Monthly Payout Export"
```

- Exit code 0 when all jobs succeed. Exit code 1 when a job fails.
- Write output to the console and to run history.
- The user can schedule the CLI with Windows Task Scheduler.

### 4.10 Maintenance

- VACUUM, ANALYZE, `PRAGMA integrity_check`, `PRAGMA foreign_key_check`.
- PRAGMA editor: journal mode, foreign keys, synchronous, cache size, page size, user version.
- Online backup to a new file (`SqliteConnection.BackupDatabase`).

### 4.11 User experience

- Main window: object explorer (left), tabbed documents (center), messages and SQL log (bottom).
- Save window layout and settings to `%APPDATA%\SqliteManager\settings.json`.
- Keyboard shortcuts: F5 execute, Ctrl+Enter execute current, Ctrl+N new query, Ctrl+S save, Ctrl+W close tab.
- Optional (last phase): dark theme.

## 5. Build phases

Complete one phase at a time. Stop at the end of each phase for review.

1. **Foundation** — Solution structure, Core services, create/open database, object explorer, read-only data grid with paging, SQL log.
2. **Query editor** — Scintilla editor, highlighting, autocomplete, tabs, execute modes, result grids, messages pane, cancel, history.
3. **Data CRUD** — Grid editing, pending changes, Write/Revert, NULL handling, filters, BLOB viewer.
4. **Table designer** — Create, alter (native and rebuild), drop, indexes, foreign keys, view and trigger editors, Script as.
5. **Import/export and jobs** — Engine, wizards, saved jobs, run history, CLI.
6. **Query designer** — Canvas, joins, criteria grid, SQL generation, save/load designs.
7. **Maintenance and polish** — Maintenance tools, PRAGMA editor, backup, EXPLAIN QUERY PLAN tree, settings, shortcuts, optional dark theme.

## 6. Working rules for Claude Code

- Read this file before each phase.
- Make a plan for the phase. Show the plan before you write code.
- Write xUnit tests for all new `Core` logic.
- Build the solution and run all tests before you report that a phase is complete.
- Do not change the scope of a phase without approval.
- Update the "Progress" section in `CLAUDE.md` at the end of each phase.
- If a requirement is not clear, ask. Do not guess.
- Never drop, delete, or overwrite user data without a confirmation step in the UI.
