using Microsoft.Data.Sqlite;
using SqliteManager.Core.Design;
using SqliteManager.Core.Schema;

namespace SqliteManager.Core.Tests;

/// <summary>
/// Applies designs to real databases and checks that no data is lost. Every test that changes a table compares
/// all rows before and after, column by column.
/// </summary>
public class AlterTableTests
{
    private static readonly string[] PeopleSetup =
    [
        "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER, photo BLOB, score REAL)",
        "INSERT INTO people VALUES (1, 'Ann', 30, x'DEADBEEF', 1.5), (2, 'Zoë 😀', NULL, NULL, -2.25), (3, '', 0, x'', NULL), (10, 'O''Brien', 99, randomblob(64), 1e300)",
    ];

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(TestDatabase db, string table, string orderBy = "rowid")
    {
        var set = await db.Session.QueryRowsAsync($"SELECT * FROM \"{table}\" ORDER BY {orderBy}");
        return set.Rows.Select(r => set.Columns.Select((c, i) => (c.Name, Value: r[i])).ToDictionary(p => p.Name, p => p.Value)).ToList();
    }

    /// <summary>Every column of <paramref name="before"/> (renamed by <paramref name="renames"/>) has the same values after.</summary>
    private static void AssertSameData(
        List<Dictionary<string, object?>> before,
        List<Dictionary<string, object?>> after,
        Dictionary<string, string>? renames = null,
        params string[] droppedColumns)
    {
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            foreach (var (column, value) in before[i])
            {
                if (droppedColumns.Contains(column))
                {
                    Assert.False(after[i].ContainsKey(column));
                    continue;
                }

                var name = renames?.GetValueOrDefault(column) ?? column;
                Assert.True(after[i].ContainsKey(name), $"Column {name} is missing.");
                Assert.Equal(value, after[i][name]);
            }
        }
    }

    private static async Task<(TableDesign Design, SchemaChangeService Service)> LoadAsync(TestDatabase db, string table)
    {
        var service = new SchemaChangeService(db.Session);
        return (await service.LoadTableAsync(table), service);
    }

    private static async Task<AlterPlan> ApplyAsync(TestDatabase db, string table, Action<TableDefinition> change)
    {
        var (design, service) = await LoadAsync(db, table);
        var edited = design.Definition.Clone();
        change(edited);
        Assert.DoesNotContain(DesignValidator.Validate(edited, design.Definition, design.RowCount, []), i => i.Severity == IssueSeverity.Error);
        var plan = AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies);
        await service.ApplyAsync(plan);
        return plan;
    }

    private static async Task<string> SqlOfAsync(TestDatabase db, string name) =>
        (await db.Session.ExecuteScalarAsync<string>("SELECT sql FROM sqlite_schema WHERE name = @n", new Dictionary<string, object?> { ["@n"] = name }))!;

    [Fact]
    public async Task Unchanged_design_needs_no_changes()
    {
        await using var db = await TestDatabase.CreateAsync([.. PeopleSetup, "CREATE INDEX ix_age ON people (age DESC)"]);
        var (design, _) = await LoadAsync(db, "people");

        var plan = AlterPlanner.PlanAlter(design.Definition, design.Definition.Clone(), design.Dependencies);

        Assert.Equal(AlterKind.None, plan.Kind);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task Native_rename_add_drop_column_and_rename_table()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var before = await RowsAsync(db, "people");

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.Name = "persons";
            t.RenameColumn(t.FindColumn("name")!, "full_name");
            t.Columns.Remove(t.FindColumn("score")!);
            t.Columns.Add(new ColumnDefinition { Name = "active", Type = "INTEGER", NotNull = true, Default = "1" });
        });

        Assert.Equal(AlterKind.Native, plan.Kind);
        Assert.Equal(["score"], plan.DroppedColumns);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == PlanStepKind.ForeignKeysOff);
        var after = await RowsAsync(db, "persons");
        AssertSameData(before, after, new() { ["name"] = "full_name" }, "score");
        Assert.All(after, r => Assert.Equal(1L, r["active"]));
    }

    [Fact]
    public async Task Native_index_add_change_and_drop()
    {
        await using var db = await TestDatabase.CreateAsync([.. PeopleSetup, "CREATE INDEX ix_age ON people (age)", "CREATE INDEX ix_name ON people (name)"]);

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.Indexes.RemoveAll(i => i.Name == "ix_name");
            t.Indexes.Single(i => i.Name == "ix_age").Terms = [IndexTerm.ForColumn("age", descending: true)];
            t.Indexes.Add(new IndexDefinition { Name = "ux_name_age", Unique = true, Terms = [IndexTerm.ForColumn("name"), IndexTerm.ForColumn("age")], Where = "age > 0" });
        });

        Assert.Equal(AlterKind.Native, plan.Kind);
        Assert.Equal(["ix_name"], plan.DroppedIndexes);
        Assert.Null(await db.Session.ExecuteScalarAsync<string>("SELECT sql FROM sqlite_schema WHERE name = 'ix_name'"));
        Assert.Contains("\"age\" DESC", await SqlOfAsync(db, "ix_age"));
        Assert.Equal("CREATE UNIQUE INDEX \"ux_name_age\" ON \"people\" (\"name\", \"age\") WHERE age > 0", await SqlOfAsync(db, "ux_name_age"));
    }

    [Fact]
    public async Task Rebuild_reorders_columns_and_keeps_every_value()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var before = await RowsAsync(db, "people");

        var plan = await ApplyAsync(db, "people", t =>
        {
            var photo = t.FindColumn("photo")!;
            t.Columns.Remove(photo);
            t.Columns.Insert(1, photo);
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        Assert.Contains("reordered", plan.RebuildReason, StringComparison.Ordinal);
        var after = await RowsAsync(db, "people");
        AssertSameData(before, after);
        Assert.Equal(["id", "photo", "name", "age", "score"], after[0].Keys);
    }

    [Fact]
    public async Task Rebuild_renames_and_changes_type_in_one_step()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var before = await RowsAsync(db, "people");

        var plan = await ApplyAsync(db, "people", t =>
        {
            var age = t.FindColumn("age")!;
            t.RenameColumn(age, "years");
            age.Type = "NUMERIC";
            age.Check = "years IS NULL OR years >= 0";
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        AssertSameData(before, await RowsAsync(db, "people"), new() { ["age"] = "years" });
        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("INSERT INTO people (name, years) VALUES ('x', -1)"));
    }

    [Theory]
    [InlineData("without rowid")]
    [InlineData("strict")]
    [InlineData("autoincrement")]
    [InlineData("composite key")]
    [InlineData("unique")]
    public async Task Rebuild_options_keep_every_value(string change)
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var before = await RowsAsync(db, "people", "id");

        var plan = await ApplyAsync(db, "people", t =>
        {
            switch (change)
            {
                case "without rowid":
                    t.WithoutRowId = true;
                    break;
                case "strict":
                    t.Strict = true;
                    t.FindColumn("photo")!.Type = "BLOB";
                    break;
                case "autoincrement":
                    t.FindColumn("id")!.AutoIncrement = true;
                    break;
                case "composite key":
                    t.FindColumn("name")!.PrimaryKeyOrder = 2;
                    break;
                case "unique":
                    t.FindColumn("name")!.Unique = true;
                    break;
            }
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        AssertSameData(before, await RowsAsync(db, "people", "id"));
    }

    [Fact]
    public async Task Turning_without_rowid_off_again_keeps_data()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE w (k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID",
            "INSERT INTO w VALUES ('a', x'01'), ('b', NULL), ('c', x'')");
        var before = await RowsAsync(db, "w", "k");

        await ApplyAsync(db, "w", t => t.WithoutRowId = false);

        AssertSameData(before, await RowsAsync(db, "w", "k"));
        Assert.DoesNotContain("WITHOUT ROWID", await SqlOfAsync(db, "w"));
    }

    [Fact]
    public async Task Rebuild_keeps_checks_collations_generated_columns_and_table_constraints()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (a TEXT COLLATE NOCASE CHECK (length(a) < 10), b INT, c INT GENERATED ALWAYS AS (b * 2) VIRTUAL, d INT AS (b + 1) STORED, UNIQUE (a, b), CHECK (b >= 0))",
            "INSERT INTO t (a, b) VALUES ('x', 1), ('Y', 2)");
        var before = await RowsAsync(db, "t");

        var plan = await ApplyAsync(db, "t", t => t.Columns.Add(new ColumnDefinition { Name = "e", Type = "TEXT", Default = "(upper('z'))" }));

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        AssertSameData(before, await RowsAsync(db, "t"));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t WHERE a = 'X'")); // NOCASE kept
        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("INSERT INTO t (a, b) VALUES ('long text here', 1)"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("INSERT INTO t (a, b) VALUES ('x', 1)")); // UNIQUE (a, b)
        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("INSERT INTO t (a, b) VALUES ('q', -1)")); // CHECK (b >= 0)
        Assert.Equal("Z", await db.Session.ExecuteScalarAsync<string>("SELECT e FROM t LIMIT 1"));
    }

    [Fact]
    public async Task Rebuild_recreates_indexes_triggers_and_dependent_views()
    {
        await using var db = await TestDatabase.CreateAsync(
            [.. PeopleSetup,
             "CREATE INDEX ix_name ON people (name)",
             "CREATE TABLE log (msg TEXT)",
             "CREATE TRIGGER trg_people AFTER INSERT ON people BEGIN INSERT INTO log VALUES ('people ' || NEW.name); END",
             "CREATE TABLE other (x)",
             "CREATE TRIGGER trg_other AFTER INSERT ON other BEGIN INSERT INTO log SELECT 'count ' || count(*) FROM people; END",
             "CREATE VIEW v_people AS SELECT name, age FROM people WHERE age IS NOT NULL",
             "CREATE VIEW v_v AS SELECT count(*) AS n FROM v_people"]);
        var (design, _) = await LoadAsync(db, "people");
        Assert.Equal(["trg_people"], design.Dependencies.OwnTriggers.Select(o => o.Name));
        Assert.Equal(["v_people", "v_v"], design.Dependencies.DependentViews.Select(o => o.Name)); // v_v uses v_people.
        Assert.Equal(["trg_other"], design.Dependencies.DependentTriggers.Select(o => o.Name));

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.RenameColumn(t.FindColumn("name")!, "full_name");
            t.FindColumn("score")!.Type = "TEXT";
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        Assert.Contains("\"full_name\"", await SqlOfAsync(db, "ix_name"));
        await db.Session.ExecuteNonQueryAsync("INSERT INTO other VALUES (1)");
        Assert.Equal("count 4", await db.Session.ExecuteScalarAsync<string>("SELECT msg FROM log"));
        Assert.Equal(3L, await db.Session.ExecuteScalarAsync<long>("SELECT n FROM v_v"));
    }

    [Fact]
    public async Task Dropping_a_column_used_by_a_trigger_or_view_is_warned()
    {
        await using var db = await TestDatabase.CreateAsync(
            [.. PeopleSetup,
             "CREATE TABLE log (msg TEXT)",
             "CREATE TRIGGER trg AFTER UPDATE ON people BEGIN INSERT INTO log VALUES (NEW.score); END",
             "CREATE VIEW v AS SELECT name, score FROM people"]);
        var (design, _) = await LoadAsync(db, "people");
        var edited = design.Definition.Clone();
        edited.Columns.Remove(edited.FindColumn("score")!);
        edited.FindColumn("age")!.Unique = true; // Forces a rebuild.

        var plan = AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies);

        Assert.Equal(2, plan.Warnings!.Count);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Trigger \"trg\" uses \"score\"", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("View \"v\" uses \"score\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_part_way_through_a_rebuild_rolls_everything_back()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var before = await RowsAsync(db, "people");
        var sqlBefore = await SqlOfAsync(db, "people");
        var (design, service) = await LoadAsync(db, "people");
        var edited = design.Definition.Clone();
        edited.FindColumn("name")!.Unique = true;
        edited.Columns.Add(new ColumnDefinition { Name = "x", Check = "x IS NULL OR x > 0", Default = "-1" }); // Every copied row fails the CHECK.

        var plan = AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies);
        await Assert.ThrowsAsync<SqliteException>(() => service.ApplyAsync(plan));

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        Assert.Equal(sqlBefore, await SqlOfAsync(db, "people"));
        AssertSameData(before, await RowsAsync(db, "people"));
        Assert.Equal(0L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_schema WHERE name LIKE '%rebuild%'"));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task Rebuild_updates_triggers_and_views_that_use_a_renamed_column()
    {
        await using var db = await TestDatabase.CreateAsync(
            [.. PeopleSetup,
             "CREATE TABLE log (msg TEXT)",
             "CREATE TRIGGER trg AFTER UPDATE OF score ON people BEGIN INSERT INTO log VALUES (NEW.score); END",
             "CREATE VIEW v AS SELECT name, score FROM people"]);

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.RenameColumn(t.FindColumn("score")!, "points");
            t.Columns.Reverse();
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        await db.Session.ExecuteNonQueryAsync("UPDATE people SET points = 7 WHERE id = 1");
        Assert.Equal(7L, await db.Session.ExecuteScalarAsync<long>("SELECT CAST(msg AS INTEGER) FROM log"));
        Assert.Equal(7.0, await db.Session.ExecuteScalarAsync<double>("SELECT points FROM v WHERE name = 'Ann'"));
    }

    [Fact]
    public async Task Renaming_a_column_used_in_a_check_stays_native()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a INT CHECK (a > 0), b INT, CHECK (b < a))", "INSERT INTO t VALUES (5, 1)");

        var plan = await ApplyAsync(db, "t", t => t.RenameColumn(t.FindColumn("a")!, "alpha"));

        Assert.Equal(AlterKind.Native, plan.Kind);
        Assert.Equal(["ALTER TABLE \"t\" RENAME COLUMN \"a\" TO \"alpha\""], plan.Steps.Where(s => s.Kind == PlanStepKind.Sql).Select(s => s.Sql));
        await Assert.ThrowsAsync<SqliteException>(() => db.Session.ExecuteNonQueryAsync("INSERT INTO t VALUES (0, -1)"));
    }

    [Fact]
    public async Task Foreign_key_violations_roll_back_and_are_reported()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE parent (id INTEGER PRIMARY KEY)",
            "INSERT INTO parent VALUES (1)",
            "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INT)",
            "INSERT INTO child VALUES (1, 1), (2, 99)");
        var sqlBefore = await SqlOfAsync(db, "child");
        var (design, service) = await LoadAsync(db, "child");
        var edited = design.Definition.Clone();
        edited.ForeignKeys.Add(new ForeignKeyDefinition { Columns = ["parent_id"], Table = "parent", ReferencedColumns = ["id"] });

        var plan = AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies);
        var ex = await Assert.ThrowsAsync<ForeignKeyViolationException>(() => service.ApplyAsync(plan));

        Assert.Single(ex.Violations);
        Assert.Contains("\"child\"", ex.Violations[0], StringComparison.Ordinal);
        Assert.Equal(sqlBefore, await SqlOfAsync(db, "child"));
        Assert.Equal(2L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM child"));
    }

    [Fact]
    public async Task Existing_violations_elsewhere_do_not_block_a_change()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE parent (id INTEGER PRIMARY KEY)",
            "CREATE TABLE child (parent_id INT REFERENCES parent (id))",
            "PRAGMA foreign_keys = OFF",
            "INSERT INTO child VALUES (5)",
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE t (a INT, b INT)");

        var plan = await ApplyAsync(db, "t", t => t.Columns.Reverse());

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_keys_setting_is_restored(bool on)
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a INT, b INT)");
        await db.Session.ExecuteNonQueryAsync($"PRAGMA foreign_keys = {(on ? "ON" : "OFF")}");

        await ApplyAsync(db, "t", t => t.Columns.Reverse());

        Assert.Equal(on ? 1L : 0L, await db.Session.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
        Assert.Contains(db.Log.GetEntries(), e => e.Sql == "PRAGMA foreign_keys = OFF");
    }

    [Fact]
    public async Task Refuses_to_run_inside_an_open_transaction()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var (design, service) = await LoadAsync(db, "people");
        var edited = design.Definition.Clone();
        edited.Columns.Reverse();
        await db.Session.ExecuteNonQueryAsync("BEGIN");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies)));

        await db.Session.ExecuteNonQueryAsync("ROLLBACK");
        Assert.StartsWith("CREATE TABLE people (id", await SqlOfAsync(db, "people"));
    }

    [Fact]
    public async Task Autoincrement_counter_is_kept_so_ids_are_not_reused()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE a (id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT)",
            "INSERT INTO a (v) VALUES ('1'), ('2'), ('3')",
            "DELETE FROM a WHERE id = 3");

        var plan = await ApplyAsync(db, "a", t => t.FindColumn("v")!.Check = "v <> ''");
        await db.Session.ExecuteNonQueryAsync("INSERT INTO a (v) VALUES ('new')");

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        Assert.Equal(4L, await db.Session.ExecuteScalarAsync<long>("SELECT id FROM a WHERE v = 'new'"));
    }

    [Fact]
    public async Task Rebuild_with_table_rename_keeps_views_working()
    {
        await using var db = await TestDatabase.CreateAsync([.. PeopleSetup, "CREATE VIEW v AS SELECT name FROM people"]);
        var before = await RowsAsync(db, "people");

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.Name = "humans";
            t.Columns.Reverse();
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        AssertSameData(before, await RowsAsync(db, "humans", "id"));
        Assert.Equal(4L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM v"));
        Assert.Equal(0L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_schema WHERE name = 'people'"));
    }

    [Fact]
    public async Task Swapping_column_names_keeps_each_columns_data()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a TEXT, b TEXT)", "INSERT INTO t VALUES ('A', 'B')");

        var plan = await ApplyAsync(db, "t", t =>
        {
            t.Columns[0].Name = "b";
            t.Columns[1].Name = "a";
        });

        Assert.Equal(AlterKind.Rebuild, plan.Kind);
        var row = (await RowsAsync(db, "t"))[0];
        Assert.Equal(("A", "B"), (row["b"], row["a"]));
    }

    [Fact]
    public async Task Dropping_an_indexed_column_falls_back_to_rebuild()
    {
        await using var db = await TestDatabase.CreateAsync([.. PeopleSetup, "CREATE INDEX ix ON people (age)"]);
        var before = await RowsAsync(db, "people");

        var plan = await ApplyAsync(db, "people", t =>
        {
            t.Columns.Remove(t.FindColumn("age")!);
            t.Indexes.Clear();
        });

        // The index is dropped with it, so a native DROP COLUMN works.
        Assert.Equal(AlterKind.Native, plan.Kind);
        AssertSameData(before, await RowsAsync(db, "people"), droppedColumns: "age");
    }

    [Fact]
    public async Task Create_table_with_indexes()
    {
        await using var db = await TestDatabase.CreateAsync();
        var design = new TableDefinition
        {
            Name = "new table",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKeyOrder = 1, AutoIncrement = true },
                new ColumnDefinition { Name = "name", Type = "TEXT", NotNull = true, Default = "''" },
            ],
            Indexes = [new IndexDefinition { Name = "ix_name", Terms = [IndexTerm.ForColumn("name")] }],
        };

        var plan = AlterPlanner.PlanCreate(design);
        await new SchemaChangeService(db.Session).ApplyAsync(plan);

        Assert.Equal(AlterKind.Create, plan.Kind);
        Assert.Equal("CREATE TABLE \"new table\" (\n    \"id\" INTEGER PRIMARY KEY AUTOINCREMENT,\n    \"name\" TEXT NOT NULL DEFAULT ''\n)", await SqlOfAsync(db, "new table"));
        Assert.NotNull(await SqlOfAsync(db, "ix_name"));
        Assert.StartsWith("BEGIN;", plan.ToScript());
    }

    [Fact]
    public async Task Script_and_object_commands()
    {
        await using var db = await TestDatabase.CreateAsync(
            [.. PeopleSetup,
             "CREATE INDEX ix ON people (name)",
             "CREATE TRIGGER trg AFTER DELETE ON people BEGIN SELECT 1; END",
             "CREATE VIEW v AS SELECT * FROM people",
             "ALTER TABLE people ADD COLUMN g INT AS (age * 2)"]);
        var schema = new SchemaService(db.Session);
        var objects = await schema.GetObjectsAsync();
        var people = objects.Single(o => o.Name == "people");

        var create = SchemaObjectCommands.ScriptCreate(people, objects);
        Assert.Contains("CREATE TABLE people", create);
        Assert.Contains("CREATE INDEX ix", create);
        Assert.Contains("CREATE TRIGGER trg", create);

        var insert = SchemaObjectCommands.ScriptInsert(await schema.GetTableDetailsAsync("people"));
        Assert.StartsWith("INSERT INTO \"people\" (\"id\", \"name\", \"age\", \"photo\", \"score\")", insert);
        Assert.Contains("NULL /* name TEXT */", insert);
        await db.Session.ExecuteNonQueryAsync(insert.Replace("NULL /* name TEXT */", "'Templated'"));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM people WHERE name = 'Templated'"));

        Assert.Equal("DROP VIEW IF EXISTS \"v\";\n", SchemaObjectCommands.ScriptDrop(objects.Single(o => o.Name == "v")));

        var service = new SchemaChangeService(db.Session);
        await service.ApplyAsync(SchemaObjectCommands.PlanRename(objects.Single(o => o.Name == "v"), "v2"));
        await service.ApplyAsync(SchemaObjectCommands.PlanRename(objects.Single(o => o.Name == "ix"), "ix2"));
        await service.ApplyAsync(SchemaObjectCommands.PlanRename(objects.Single(o => o.Name == "trg"), "trg2"));
        await service.ApplyAsync(SchemaObjectCommands.PlanRename(people, "folks"));
        var names = (await schema.GetObjectsAsync()).Select(o => o.Name).ToList();
        Assert.Contains("v2", names);
        Assert.Contains("ix2", names);
        Assert.Contains("trg2", names);
        Assert.Contains("folks", names);
        Assert.Equal(5L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM v2"));

        await service.ApplyAsync(SchemaObjectCommands.PlanDrop((await schema.GetObjectsAsync()).Single(o => o.Name == "v2")));
        Assert.DoesNotContain("v2", (await schema.GetObjectsAsync()).Select(o => o.Name));
    }

    [Fact]
    public async Task View_and_trigger_replace_plans()
    {
        await using var db = await TestDatabase.CreateAsync([.. PeopleSetup, "CREATE TABLE log (m)"]);
        var service = new SchemaChangeService(db.Session);
        var view = new ViewDefinition { Name = "adults", Select = "SELECT name FROM people WHERE age >= 18" };

        await service.ApplyAsync(SchemaObjectCommands.PlanReplace(SchemaObjectType.View, null, view.ToSql()));
        view.Select = "SELECT name FROM people WHERE age >= 50";
        await service.ApplyAsync(SchemaObjectCommands.PlanReplace(SchemaObjectType.View, "adults", view.ToSql()));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM adults"));

        var bad = SchemaObjectCommands.PlanReplace(SchemaObjectType.View, "adults", "CREATE VIEW adults AS SELEC nope");
        await Assert.ThrowsAsync<SqliteException>(() => service.ApplyAsync(bad));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM adults")); // Old view kept.

        var trigger = new TriggerDefinition { Name = "t1", Timing = TriggerTiming.After, Event = TriggerEvent.Delete, Table = "people", Body = "INSERT INTO log VALUES (OLD.name)" };
        await service.ApplyAsync(SchemaObjectCommands.PlanReplace(SchemaObjectType.Trigger, null, trigger.ToSql()));
        await db.Session.ExecuteNonQueryAsync("DELETE FROM people WHERE id = 1");
        Assert.Equal("Ann", await db.Session.ExecuteScalarAsync<string>("SELECT m FROM log"));
    }

    [Fact]
    public async Task Load_rejects_tables_the_parser_cannot_read()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a TEXT, PRIMARY KEY (a COLLATE NOCASE))");

        await Assert.ThrowsAsync<SqlParseException>(() => new SchemaChangeService(db.Session).LoadTableAsync("t"));
    }

    [Fact]
    public async Task Plan_script_shows_every_step()
    {
        await using var db = await TestDatabase.CreateAsync(PeopleSetup);
        var (design, _) = await LoadAsync(db, "people");
        var edited = design.Definition.Clone();
        edited.Columns.Reverse();

        var script = AlterPlanner.PlanAlter(design.Definition, edited, design.Dependencies).ToScript();

        Assert.StartsWith("PRAGMA foreign_keys = OFF;\nBEGIN;\nCREATE TABLE \"people__rebuild_", script.ReplaceLineEndings("\n"));
        Assert.Contains("INSERT INTO \"people__rebuild_", script);
        Assert.Contains("DROP TABLE \"people\";", script);
        Assert.Contains("PRAGMA foreign_key_check;", script);
        Assert.EndsWith("COMMIT;\nPRAGMA foreign_keys = <previous setting>;\n", script.ReplaceLineEndings("\n"));
    }
}
