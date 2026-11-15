# CLAUDE.md

Full specification: `docs/SPEC.md`.

## Architecture

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

## Working rules

- Machine-specific notes are in `CLAUDE.local.md` (not in git).
- Read `docs/SPEC.md` before each phase.
- Make a plan for the phase. Show the plan before you write code.
- Write xUnit tests for all new `Core` logic.
- Build the solution and run all tests before you report that a phase is complete.
- Do not change the scope of a phase without approval.
- Update the "Progress" section in `CLAUDE.md` at the end of each phase.
- If a requirement is not clear, ask. Do not guess.
- Never drop, delete, or overwrite user data without a confirmation step in the UI.

## Progress

### Phase 1 — Foundation: done (2026-11-12), awaiting review
- Solution `SqliteManager.sln` (classic .sln format) with Core, App, Cli, Core.Tests. `Directory.Build.props` sets nullable, implicit usings, warnings as errors.
- Packages: `Microsoft.Data.Sqlite` 11.0.0 (Core), xUnit (Tests).
- Core:
  - `Sql/SqlIdentifier`: identifier quoting.
  - `Logging/SqlLog`: thread-safe in-memory SQL log (5000 entries).
  - `Data/DatabaseSession`: the only path for SQL. Serialized, async, logs every statement.
  - `Data/DatabaseManager`: create (refuses existing files), open, open read-only, several databases at once, close.
  - `Schema/SchemaService`: objects plus table details.
  - `Data/TableDataService`: row count and LIMIT/OFFSET pages.
  - `Data/PagedRowCache`: LRU page cache for virtual grids.
- App: custom-drawn Office-style ribbon (`Ui/Ribbon.cs`) with a File menu plus Home and View tabs. Object explorer (tables expand lazily into columns, keys, and indexes), read-only virtual-mode data grid (NULL and BLOB shown in gray italic), Messages pane, SQL Log pane. Icons are drawn at run time; no image files.
- Cli: placeholder that prints usage. The job runner comes in Phase 5.
- Tests: 53 passing, including opening a 1,000,000-row table in under 2 seconds.
- Deferred by agreement: recent-files list (Phase 7, with settings), `ATTACH DATABASE` (later phase).

### Phase 2 — Query editor: done (2026-11-12), awaiting review
- Packages: `Scintilla5.NET` 7.1.1 (App; ships net8.0-windows, runs on net10).
- Decisions agreed with the user:
  - Open/Save `.sql` is in Phase 2.
  - Row limit is 100,000 per result set by default. Users change it in File > Options, which saves `queryRowLimit` to `%APPDATA%\SqliteManager\settings.json`.
  - History lives in `%APPDATA%\SqliteManager\query-history.jsonl` and keeps the last 1,000 runs.
  - Ctrl+N, Ctrl+S, and Ctrl+W are included now.
  - A script stops at the first failing statement.
- Core:
  - `Sql/SqlScriptSplitter`: splits statements (quotes, comments, trigger bodies) and finds the statement at the caret.
  - `Sql/SqlKeywords`.
  - `Query/QueryExecutor`: runs statements one by one and reports progress. Error lines come from the token SQLite names, because SQLitePCLRaw has no `sqlite3_error_offset`.
  - `Query/CompletionProvider`: keywords, tables, views, functions, and `alias.`/`table.` columns.
  - `Query/QueryHistoryStore`.
  - `IO/AtomicFile`, `IO/SqlFileService`, `IO/AppPaths`.
  - `Settings/AppSettings` + `SettingsStore`.
  - `SchemaService.GetSnapshotAsync`.
  - `DatabaseSession.ExecuteStatementAsync`.
- Cancel: any cancelled token passed to `DatabaseSession` calls `sqlite3_interrupt`, but only during that call's own statement.
- App:
  - `Documents/QueryDocument` (editor, result tabs, Messages with double-click to jump to the line).
  - `Documents/SqlEditor` (Scintilla: SQL lexer, folding, brace match, autocomplete on `.`, after 2 characters, or Ctrl+Space).
  - `Documents/ResultGrid` and shared `Ui/SqlDataGrid`.
  - `Panes/HistoryView` ("Query History" tab in the bottom pane).
  - `OptionsDialog`.
  - Ribbon "Query" tab: Execute / Selection / Current / Cancel, database picker, Open / Save / Save As.
  - Explorer context menu: New Query.
- Query tabs share their database's single connection, so transactions and temp tables carry across runs. Closing a database leaves its query tabs open with no database. Closing a modified query tab or the app asks Save / Don't Save / Cancel.
- Tests: 128 passing.
- Post-review tweak (user request): document tabs have a close X (`Ui/ClosableTabControl`). Ctrl+F4 closes the active tab, the same as Ctrl+W.
- Still in later phases: `EXPLAIN QUERY PLAN` tree (7), exporting result grids (5).

### Phase 3 — Data CRUD: done (2026-11-12), awaiting review
- Decisions agreed with the user:
  - Sort (click a header: ascending, descending, off) and Ctrl+C as TSV with headers are included.
  - Write asks for confirmation only when the batch deletes rows.
  - Numbers are shown and edited in invariant format (1234.5). This changed the Phase 1 and 2 grids too.
  - Views stay read-only.
- Core:
  - `Data/TableQuery.cs` (filters, sort, `RowKey`, `TableSnapshot`, `TableRow`).
  - `Data/FilterParser`: contains by default, plus `=`, `<>`/`!=`, `>`, `>=`, `<`, `<=`, `IS NULL`, `IS NOT NULL`.
  - `Data/ValueParser`: affinity rules, text to value, invariant `FormatForEdit`.
  - `Data/PendingChanges`: updates, inserts, deletes; builds `BatchCommand`s ordered deletes, updates, inserts.
  - `Data/BlobService`, `Data/ClipboardFormatter`, `IO/AtomicFile.WriteAllBytesAsync`.
- `TableDataService` was reworked. `OpenAsync(name, query)` returns a `TableSnapshot`; `GetPageAsync` returns `TableRow`s with keys. The row key is the rowid (first free name of rowid, `_rowid_`, `oid`) or the PK columns for WITHOUT ROWID tables. A filtered or sorted rowid table reads its ordered rowid list once and then pages by `rowid IN (...)`.
- `PagedRowCache` is now generic: `PagedRowCache<TRow>`.
- `DatabaseSession.ExecuteBatchAsync` runs commands inside `SAVEPOINT sqlite_manager_write`. Every UPDATE and DELETE must affect exactly 1 row, otherwise everything rolls back (`BatchExecutionException`). It reports when it joined an open user transaction. Internally, `RunLockedAsync` + `Execute` replaced the old `RunAsync` body.
- App:
  - `Documents/TableDataView` (editable virtual grid, pending marks ✎/★/✕ with yellow, green, and strikethrough styling).
  - `Documents/FilterBar`, `Documents/BlobViewerDialog` (Hex, Text, Image; Load from file, Save to file, Set NULL).
  - `Ui/SqlDataGrid` gained `CellStateProvider` and the `DefaultValue` placeholder.
  - Ribbon "Data" tab. Ctrl+S on a data tab writes changes, Alt+Del sets NULL, Del deletes selected rows.
  - Closing a data tab, its database, or the app with pending changes asks Write / Discard / Cancel.
- Tests: 219 passing at the end of Phase 3. Also exercised in the running app: create a database, run DDL and INSERTs, edit cells, Set NULL, delete, insert, write with the delete confirmation, filter, sort, the BLOB viewer, Ctrl+C, the close prompt, and Revert.

### Phase 4 — Table designer: done (2026-11-12), awaiting review
- Decisions agreed with the user:
  - A rebuild puts `foreign_keys` back to its previous value, not always ON.
  - Rename is included: tables use native RENAME; views, indexes, and triggers are dropped and re-created.
  - Script as opens in a new query tab, and INSERT is a `NULL /* name TYPE */` template.
  - Table-level UNIQUE and CHECK constraints are kept and edited as text on the Options tab.
- Finding: the `e_sqlite3` bundle starts connections with **foreign keys ON**. PRAGMA foreign_keys = 1 by default, so FK rules are enforced in all phases.
- Core, `Design/`:
  - `SqlTokenizer` + `TokenCursor`.
  - `TableDefinition` model.
  - `CreateTableParser` (also `IndexParser`, `ViewDefinition`, `TriggerDefinition`). It keeps constraint names, ON CONFLICT clauses, and generated columns. Anything it cannot read raises `SqlParseException`, and the designer then opens read-only.
  - `TableSqlGenerator`, `DesignValidator`, `SqlRewriter`.
  - `AlterPlanner`: native vs rebuild, with `AlterPlan.ToScript()` for the preview.
  - `SchemaChangeService`: loads designs and runs plans. `SchemaObjectCommands`: script, rename, drop, replace.
- How the rebuild works:
  - It refuses to run inside an open transaction.
  - Renamed columns are renamed natively first, so SQLite updates triggers and views.
  - Dependent views and triggers are found at run time, including views that use those views. They are dropped and re-created in dependency order.
  - The AUTOINCREMENT counter is kept.
  - Only new foreign-key problems roll the change back.
- `DatabaseSession.RunExclusiveAsync` / `ISqlRunner` run multi-step operations under one lock, with every statement logged.
- App:
  - `Documents/SchemaEditorBase`: live SQL preview, issues list, Apply/Revert, close prompt.
  - `TableDesigner` (Columns, Indexes with a column picker, Foreign Keys, Options), `ViewEditor`, `TriggerEditor` (fields or "Edit as SQL").
  - Explorer menu: Design, Script as, New Table/View/Trigger, Rename (F2, in place), Drop (Del, with confirmation).
  - Ribbon: "Design" tab, and Home > Create.
  - Apply asks first when columns or indexes would be dropped. Apply, Rename, and Drop are refused while the object's data tab has unwritten changes.
- Tests: 291 passing at the end of Phase 4, including before/after data comparisons for every rebuild path. Also exercised in the running app: designer load, native add-column preview, rebuild preview and apply (data intact), Script as CREATE, rename with an open designer and data tab, drop-view confirmation, view editor apply, and the trigger editor.

### Phase 5 — Import/export and jobs: done (2026-11-12), awaiting review
- Decisions agreed with the user:
  - The app asks before truncate or replace imports and before export overwrites. The CLI runs jobs as configured, without asking.
  - Script transactionMode `single` runs all steps in one transaction (a failing `stop` step rolls everything back); `none` lets each statement commit. A `runJob` on the same database joins the transaction.
  - Bulk statements are logged once with a count.
  - CSV export defaults to UTF-8 with BOM.
  - The .xls test file was made once with Excel: `tests/SqliteManager.Core.Tests/Fixtures/reps.xls`.
- Packages: CsvHelper is **pinned to 33.1.0** because 34.0 is a rewrite with a new API. Also ClosedXML 0.105.1, ExcelDataReader + .DataSet 3.9.0, System.Text.Encoding.CodePages.
- Core:
  - `Jobs/`: JobModels (matches the spec JSON exactly; the spec example round-trips), JobStore, RunHistoryStore, JobRunner, CommandLine.
  - `Transfer/`: SourceReaders (CSV, TSV, xlsx, xls, JSON), ImportValueConverter, ImportEngine (SAVEPOINT, load modes, upsert, skip/rollback, error file), ExportEngine (csv, tsv, xlsx, json, sql writers; tokens; if-exists; temp file then replace), TextEncodings.
- `ISqlRunner` gained `Prepare` (reused statement, logged once), `QueryEach` (streaming), and an `Execute` with a timeout token.
- Cli: `Program.cs` calls `CommandLine.RunAsync`. Ctrl+C cancels.
- App, `Transfer/`:
  - ImportWizard, ExportWizard, JobsDocument (the Jobs tab), ScriptJobEditor.
  - JobsService (runs jobs, asks for parameters, confirms destructive actions), ParameterDialog.
  - Ribbon "External Data" tab. Explorer menu: Import Data / Export Data. Result tabs: Export... (re-runs SELECT, WITH, or VALUES).
- Tests: 372 passing, including 100k CSV rows imported in under 10 s.
- Checked by hand: the CLI exe (list, run, exit codes 0 and 1, history), and the import wizard in the app (CSV with ; delimiter, de-DE numbers, dd.MM.yyyy dates, preview, new table, run).
- Also checked by hand afterwards: the export wizard (SQL with an @Min parameter, CSV with {date}, parameter prompt), the Jobs tab, and the script job editor (opened and cancelled; nothing saved).
- Bugs found in that check and fixed:
  - The export wizard's "table" and "SQL" options could both be selected, and the SQL box stayed disabled.
  - Enter in the parameter dialog did not run the job.
  - Esc while autocomplete is open could close the dialog around the SQL editor.

### Phase 6 — Query designer: done (2026-11-13), awaiting review
- The user approved the plans for Phases 6 and 7 together ("go with your recommendations, complete all remaining phases").
- Decisions (my recommendations):
  - Criteria work like Access. `abc` means `= 'abc'`. Also supported: `>5`, `<>x`, `LIKE A%`, `IN (...)`, `BETWEEN a AND b`, `IS [NOT] NULL`.
  - Criteria values become literals in the SQL, because the SQL is meant to be sent to a query tab. Text is escaped with `''`; numbers, quoted text, `NULL`, `CURRENT_DATE`, `@params`, and expressions with `(` are kept as typed; `"x"` becomes `'x'`.
  - Criteria rows are combined with OR; the cells within a row with AND.
  - In Totals mode, criteria on Where fields go to WHERE. All other criteria go to HAVING, as in Access.
  - A left join can be turned either way. When the second table is the one kept, the SQL uses `RIGHT JOIN`.
  - Two or more join lines between the same two tables are combined into one `ON ... AND ...`.
  - Tables without a join line get a `CROSS JOIN`.
  - Self-joins use aliases (`employees_1`).
  - Extras: Unique Values (DISTINCT) and Limit.
- Core, `QueryDesign/`:
  - QueryDesignModels: `QueryDesignDocument`, `DesignTable`, `DesignJoin`, and `DesignField`.
  - CriteriaParser, QuerySqlGenerator, and JoinSuggester (finds joins from foreign keys in both directions, with composite keys and the referenced PK when no target columns are given).
  - QueryDesignSerializer: `.qdesign` JSON with camelCase enums and atomic save.
- App, `QueryDesigner/`:
  - `DesignCanvas` (custom GDI+ panel):
    - Table boxes with column check boxes and a `*` row; PK and FK columns in bold.
    - Move a box by its header. Close it with ×, or right-click for Remove / Add All Columns.
    - Drag one column onto another to join them.
    - Click or right-click a join line to choose Inner, Left (either direction), or Cross, or to delete it. A left join shows an arrow.
    - Drop tables from the explorer. Boxes with many columns scroll with the mouse wheel.
  - `CriteriaGrid`: Field, Table, Alias, Total (Totals mode only), Show, Sort, Sort Order, Criteria, and Or columns.
  - `QueryDesignerDocument`:
    - Toolbar: Add Table, Totals, Unique Values, and Limit.
    - Bottom tabs: Fields, SQL (live preview with the list of problems), and Results (with Export...).
  - Ribbon:
    - A "Query Design" tab: Run (F5), Cancel, Send to Query Tab, Add Table, Open Design, Save, and Save As (Ctrl+S).
    - Home > Query Design.
    - File menu: New / Open Query Design.
  - Explorer: drag tables or views, and a "New Query Design" menu item.
  - A modified design asks Save / Don't Save / Cancel on close. Closing the database closes its designs.
- `Ui/Theme` colors are now properties with light and dark values, ready for Phase 7.
- Tests: 437 passing.
- Checked in the running app:
  - Drag tables from the explorer; the join from the foreign key is drawn.
  - Tick columns, criteria, a left join from the line menu, and Run.
  - Totals with Sum and HAVING, moving a box, a join by dragging columns, and deleting a join.
  - Save As `.qdesign` and Send to Query Tab.
- Fixed during the check: a combo cell in the criteria grid needed two clicks to open. One click now opens it.

### Phase 7 — Maintenance and polish: done (2026-11-13), awaiting review
- Decisions (my recommendations, approved together with Phase 6):
  - Check results:
    - When there are no problems, a check shows a message box.
    - When there are problems, it opens a result tab.
  - Changing PRAGMAs:
    - The PRAGMA editor shows the SQL before Apply.
    - A new page size runs VACUUM, and it leaves WAL first if needed. Page size cannot change while staying in WAL.
  - Backup:
    - Backup writes to a temp file, then replaces the target. The Save dialog asks before overwriting.
    - Backup refuses to run while a transaction is open (SQLite would report "locked").
  - ATTACH:
    - ATTACH refuses missing files (SQLite would create an empty one), the names main and temp, and duplicates.
    - Attached databases show under "Attached Databases" in the explorer, with Detach.
  - Settings and theme:
    - The theme is chosen in File > Options and takes effect after a restart.
    - Settings load before the window is created, so the theme and layout apply at startup.
- Core:
  - `Maintenance/MaintenanceService`: VACUUM (size before and after), ANALYZE, integrity_check / quick_check, and foreign_key_check.
  - `Maintenance/PragmaService`: read, plan, apply, and validate. PRAGMA values cannot be parameters, so every value is checked against fixed lists.
  - `DatabaseSession.BackupToAsync`: online backup with `SqliteConnection.BackupDatabase`, logged.
  - `Data/AttachService`: list, attach (path as a parameter, quoted name), detach, and suggested names.
  - `Query/QueryPlan`: runs EXPLAIN QUERY PLAN, binds `@params` to NULL, and builds a tree by parent id. Full scans are flagged.
  - Settings now hold recent files (10, newest first, unique), `WindowLayout` (bounds, maximized, pane sizes and visibility), and Theme.
- App:
  - Ribbon "Database Tools" tab: Vacuum, Analyze, Integrity Check, Foreign Key Check, PRAGMA Settings (`Tools/PragmaDialog`), Back Up, Attach, and Detach (a drop-down list).
  - Query tab: "Explain Plan" (Ctrl+L) shows a "Query Plan" tree tab, with full scans in the warning color.
  - File > Recent Files: missing files offer to be removed; Clear List. Ctrl+O opens a database.
  - The window bounds, maximized state, splitter sizes, and pane visibility are saved on exit and restored at start (only if still on a screen).
  - Dark theme:
    - `Theme` colors and `Application.SetColorMode(Dark)`.
    - The SQL editor has a dark palette.
    - Grids, wizards, dialogs, and menus use theme colors.
    - `ClosableTabControl` now paints its whole tab strip.
    - An empty document area shows the workspace color.
- Tests: 465 passing.
- Checked in the running app, in the light and dark themes:
  - Ctrl+O, the integrity check, the PRAGMA editor (user_version applied), Back Up, Attach (and the explorer folder), and Explain Plan on a query across the attached database.
  - The saved window layout and recent files, opening from Recent Files, and the query designer and data grid in dark mode.
- Fixed during the check:
  - The selected ribbon tab's text was hard to read in dark mode.
  - The empty document area and the document tab strip stayed light in dark mode.
- Not done: Detach is in the ribbon and the explorer, but autocomplete does not list tables of attached databases.
