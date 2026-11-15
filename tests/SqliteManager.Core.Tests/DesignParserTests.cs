using SqliteManager.Core.Design;

namespace SqliteManager.Core.Tests;

public class DesignParserTests
{
    private const string RichTable = """
        CREATE TABLE IF NOT EXISTS main."order items" (
            -- the key
            id INTEGER CONSTRAINT pk_id PRIMARY KEY ON CONFLICT REPLACE AUTOINCREMENT,
            [sku code] VARCHAR(20) NOT NULL COLLATE NOCASE UNIQUE ON CONFLICT IGNORE,
            qty INTEGER DEFAULT -5 CHECK (qty <> 0),
            price DECIMAL(10, 2) DEFAULT (round(1.5 * 2, 2)),
            note TEXT DEFAULT 'it''s new',
            created TEXT DEFAULT CURRENT_TIMESTAMP,
            blobby DEFAULT x'00ff' NULL,
            total REAL GENERATED ALWAYS AS (qty * price) STORED,
            customer_id INT REFERENCES customers(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
            CONSTRAINT one_note UNIQUE (note, created) ON CONFLICT ABORT,
            CHECK (price >= 0),
            FOREIGN KEY ([sku code]) REFERENCES products (code) ON UPDATE SET NULL
        )
        """;

    [Fact]
    public void Parses_columns_and_constraints()
    {
        var t = CreateTableParser.Parse(RichTable);

        Assert.Equal("order items", t.Name);
        Assert.Equal(["id", "sku code", "qty", "price", "note", "created", "blobby", "total", "customer_id"], t.Columns.Select(c => c.Name));
        Assert.All(t.Columns, c => Assert.Equal(c.Name, c.OriginalName));

        var id = t.Columns[0];
        Assert.Equal(("INTEGER", 1, true), (id.Type, id.PrimaryKeyOrder, id.AutoIncrement));
        Assert.Equal("pk_id", id.ConstraintNames[ColumnConstraintKind.PrimaryKey]);
        Assert.Equal("ON CONFLICT REPLACE", id.ConflictClauses[ColumnConstraintKind.PrimaryKey]);

        var sku = t.Columns[1];
        Assert.Equal(("VARCHAR(20)", true, true, "NOCASE"), (sku.Type, sku.NotNull, sku.Unique, sku.Collation));
        Assert.Equal("ON CONFLICT IGNORE", sku.ConflictClauses[ColumnConstraintKind.Unique]);

        Assert.Equal(("-5", "qty <> 0"), (t.Columns[2].Default, t.Columns[2].Check));
        Assert.Equal(("DECIMAL(10, 2)", "(round(1.5 * 2, 2))"), (t.Columns[3].Type, t.Columns[3].Default));
        Assert.Equal("'it''s new'", t.Columns[4].Default);
        Assert.Equal("CURRENT_TIMESTAMP", t.Columns[5].Default);
        Assert.Equal(("", "x'00ff'", false), (t.Columns[6].Type, t.Columns[6].Default, t.Columns[6].NotNull));

        var total = t.Columns[7];
        Assert.Equal("GENERATED ALWAYS AS (qty * price) STORED", total.Extra);
        Assert.True(total.IsGenerated);
        Assert.False(t.Columns[2].IsGenerated);

        Assert.Equal(["CONSTRAINT one_note UNIQUE (note, created) ON CONFLICT ABORT", "CHECK (price >= 0)"], t.TableConstraints);

        Assert.Equal(2, t.ForeignKeys.Count);
        var fk1 = t.ForeignKeys[0];
        Assert.Equal(["customer_id"], fk1.Columns);
        Assert.Equal(["id"], fk1.ReferencedColumns);
        Assert.Equal(("customers", "CASCADE", "DEFERRABLE INITIALLY DEFERRED"), (fk1.Table, fk1.OnDelete, fk1.Extra));
        var fk2 = t.ForeignKeys[1];
        Assert.Equal(["sku code"], fk2.Columns);
        Assert.Equal(["code"], fk2.ReferencedColumns);
        Assert.Equal(("products", "SET NULL"), (fk2.Table, fk2.OnUpdate));
    }

    [Fact]
    public async Task Generated_sql_round_trips_and_runs()
    {
        var parsed = CreateTableParser.Parse(RichTable);
        var sql = TableSqlGenerator.CreateTable(parsed);
        var reparsed = CreateTableParser.Parse(sql);

        Assert.Equal(sql, TableSqlGenerator.CreateTable(reparsed));

        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE customers (id INTEGER PRIMARY KEY)",
            "CREATE TABLE products (code TEXT PRIMARY KEY)");
        await db.Session.ExecuteNonQueryAsync(sql);
        var stored = await db.Session.ExecuteScalarAsync<string>("SELECT sql FROM sqlite_schema WHERE name = 'order items'");
        Assert.Equal(sql, TableSqlGenerator.CreateTable(CreateTableParser.Parse(stored!)));
    }

    [Fact]
    public void Composite_primary_key_and_options()
    {
        var t = CreateTableParser.Parse("CREATE TABLE w (a TEXT, b INTEGER, c, CONSTRAINT pk PRIMARY KEY (b DESC, a) ON CONFLICT FAIL) WITHOUT ROWID, STRICT;");

        Assert.Equal(["b", "a"], t.PrimaryKeyColumns.Select(c => c.Name));
        Assert.True(t.Columns[1].PrimaryKeyDescending);
        Assert.Equal(("pk", "ON CONFLICT FAIL"), (t.PrimaryKeyConstraintName, t.PrimaryKeyConflict));
        Assert.True(t.WithoutRowId);
        Assert.True(t.Strict);
        Assert.Contains("CONSTRAINT \"pk\" PRIMARY KEY (\"b\" DESC, \"a\") ON CONFLICT FAIL", TableSqlGenerator.CreateTable(t));
        Assert.EndsWith(") WITHOUT ROWID, STRICT", TableSqlGenerator.CreateTable(t));
    }

    [Fact]
    public void Columns_named_like_keywords_are_not_table_constraints()
    {
        var t = CreateTableParser.Parse("CREATE TABLE k (\"check\" TEXT, \"primary\" INT, [unique] INT, \"key\")");

        Assert.Equal(["check", "primary", "unique", "key"], t.Columns.Select(c => c.Name));
        Assert.Empty(t.TableConstraints);
    }

    [Theory]
    [InlineData("CREATE VIRTUAL TABLE f USING fts5(a)")]
    [InlineData("CREATE TABLE t (a TEXT, PRIMARY KEY (a COLLATE NOCASE))")]
    [InlineData("CREATE TABLE t (a TEXT NOT WEIRD)")]
    [InlineData("CREATE TABLE t (a TEXT")]
    [InlineData("CREATE TABLE t (a TEXT) EXTRA")]
    public void Unsupported_sql_raises_parse_errors(string sql)
    {
        Assert.Throws<SqlParseException>(() => CreateTableParser.Parse(sql));
    }

    [Fact]
    public void Type_names_with_several_words()
    {
        var t = CreateTableParser.Parse("CREATE TABLE t (a UNSIGNED BIG INT NOT NULL, b DOUBLE PRECISION, c)");

        Assert.Equal(["UNSIGNED BIG INT", "DOUBLE PRECISION", ""], t.Columns.Select(c => c.Type));
    }

    [Fact]
    public void Index_parser_reads_columns_expressions_and_where()
    {
        var index = IndexParser.Parse("CREATE UNIQUE INDEX IF NOT EXISTS \"ix one\" ON t (a, \"b c\" DESC, d COLLATE NOCASE ASC, lower(e), f + 1 DESC) WHERE a IS NOT NULL;");

        Assert.Equal(("ix one", "ix one", true, "a IS NOT NULL"), (index.Name, index.OriginalName, index.Unique, index.Where));
        Assert.Equal(
            [
                IndexTerm.ForColumn("a"),
                IndexTerm.ForColumn("b c", descending: true),
                new IndexTerm("d", null, "NOCASE", false),
                new IndexTerm(null, "lower(e)"),
                new IndexTerm(null, "f + 1 DESC"),
            ],
            index.Terms);
        Assert.Equal(
            "CREATE UNIQUE INDEX \"ix one\" ON \"t\" (\"a\", \"b c\" DESC, \"d\" COLLATE NOCASE, lower(e), f + 1 DESC) WHERE a IS NOT NULL",
            TableSqlGenerator.CreateIndex(index, "t"));
        Assert.Equal("a, \"b c\" DESC, d COLLATE NOCASE, lower(e), f + 1 DESC", TableSqlGenerator.IndexTermsDisplay(index.Terms));
        Assert.Equal(index.Terms, IndexParser.ParseTermList(TableSqlGenerator.IndexTermsDisplay(index.Terms)));
    }

    [Fact]
    public void View_parse_and_generate()
    {
        var view = ViewDefinition.Parse("CREATE VIEW IF NOT EXISTS \"v 1\" (x, \"y\") AS\n  SELECT a, b FROM t WHERE a > 1;");

        Assert.Equal(("v 1", "SELECT a, b FROM t WHERE a > 1"), (view.Name, view.Select));
        Assert.Equal(["x", "y"], view.ColumnNames);
        Assert.Equal("CREATE VIEW \"v 1\" (\"x\", \"y\") AS\nSELECT a, b FROM t WHERE a > 1", view.ToSql());
    }

    [Fact]
    public async Task Trigger_parse_and_generate()
    {
        const string sql = """
            CREATE TRIGGER IF NOT EXISTS trg AFTER UPDATE OF name, "age" ON people FOR EACH ROW
            WHEN NEW.age > (SELECT 1) BEGIN
              INSERT INTO log VALUES ('updated; ok');
              UPDATE people SET name = CASE WHEN 1 THEN name END WHERE id = NEW.id;
            END
            """;
        var trigger = TriggerDefinition.Parse(sql);

        Assert.Equal(("trg", TriggerTiming.After, TriggerEvent.Update, "people", true), (trigger.Name, trigger.Timing, trigger.Event, trigger.Table, trigger.ForEachRow));
        Assert.Equal(["name", "age"], trigger.UpdateColumns);
        Assert.Equal("NEW.age > (SELECT 1)", trigger.When);
        Assert.StartsWith("INSERT INTO log", trigger.Body);
        Assert.EndsWith("WHERE id = NEW.id;", trigger.Body);

        var generated = trigger.ToSql();
        var again = TriggerDefinition.Parse(generated);
        Assert.Equal(generated, again.ToSql());

        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, age INT)",
            "CREATE TABLE log (msg TEXT)");
        await db.Session.ExecuteNonQueryAsync(generated);
        await db.Session.ExecuteNonQueryAsync("INSERT INTO people VALUES (1, 'a', 1); UPDATE people SET age = 5");
        Assert.Equal("updated; ok", await db.Session.ExecuteScalarAsync<string>("SELECT msg FROM log"));
    }

    [Theory]
    [InlineData(TriggerTiming.Before, TriggerEvent.Delete, "CREATE TRIGGER \"t\" BEFORE DELETE ON \"x\" FOR EACH ROW\nBEGIN\n    SELECT 1;\nEND")]
    [InlineData(TriggerTiming.InsteadOf, TriggerEvent.Insert, "CREATE TRIGGER \"t\" INSTEAD OF INSERT ON \"x\" FOR EACH ROW\nBEGIN\n    SELECT 1;\nEND")]
    [InlineData(TriggerTiming.Unspecified, TriggerEvent.Update, "CREATE TRIGGER \"t\" UPDATE ON \"x\" FOR EACH ROW\nBEGIN\n    SELECT 1;\nEND")]
    public void Trigger_generation_adds_missing_semicolon(TriggerTiming timing, TriggerEvent ev, string expected)
    {
        var trigger = new TriggerDefinition { Name = "t", Table = "x", Timing = timing, Event = ev, Body = "SELECT 1" };

        Assert.Equal(expected, trigger.ToSql());
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1", "CREATE VIEW \"new name\" AS SELECT 1")]
    [InlineData("CREATE TEMP VIEW IF NOT EXISTS main.v AS SELECT 1", "CREATE TEMP VIEW IF NOT EXISTS \"new name\" AS SELECT 1")]
    [InlineData("CREATE UNIQUE INDEX [ix] ON t (a)", "CREATE UNIQUE INDEX \"new name\" ON t (a)")]
    [InlineData("CREATE TRIGGER \"tr\" AFTER INSERT ON t BEGIN SELECT 1; END", "CREATE TRIGGER \"new name\" AFTER INSERT ON t BEGIN SELECT 1; END")]
    public void RenameInCreateStatement(string sql, string expected)
    {
        Assert.Equal(expected, SchemaObjectCommands.RenameInCreateStatement(sql, "new name"));
    }

    [Fact]
    public void Tokenizer_handles_literals_names_and_comments()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT x'0A', 'it''s', \"a\"\"b\", [c d], `e`, 1.5e3, 0x1F, ?1, :p -- c\n /* d */ <= ||");

        Assert.Equal(
            [SqlTokenKind.Word, SqlTokenKind.Blob, SqlTokenKind.Symbol, SqlTokenKind.String, SqlTokenKind.Symbol, SqlTokenKind.QuotedName, SqlTokenKind.Symbol,
             SqlTokenKind.QuotedName, SqlTokenKind.Symbol, SqlTokenKind.QuotedName, SqlTokenKind.Symbol, SqlTokenKind.Number, SqlTokenKind.Symbol,
             SqlTokenKind.Number, SqlTokenKind.Symbol, SqlTokenKind.Variable, SqlTokenKind.Symbol, SqlTokenKind.Variable, SqlTokenKind.Symbol, SqlTokenKind.Symbol],
            tokens.Select(t => t.Kind));
        Assert.Equal("it's", tokens[3].Value);
        Assert.Equal("a\"b", tokens[5].Value);
        Assert.Equal("c d", tokens[7].Value);
        Assert.Equal(["<=", "||"], tokens[^2..].Select(t => t.Text));
        Assert.Throws<SqlParseException>(() => SqlTokenizer.Tokenize("'open"));
    }
}
