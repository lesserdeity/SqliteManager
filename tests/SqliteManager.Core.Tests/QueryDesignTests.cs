using System.Text.Json;
using SqliteManager.Core.QueryDesign;
using SqliteManager.Core.Schema;

namespace SqliteManager.Core.Tests;

public class CriteriaParserTests
{
    [Theory]
    [InlineData("abc", "f = 'abc'")]
    [InlineData("5", "f = 5")]
    [InlineData("-2.5", "f = -2.5")]
    [InlineData(">5", "f > 5")]
    [InlineData(">= 10", "f >= 10")]
    [InlineData("<>x", "f <> 'x'")]
    [InlineData("!=x", "f <> 'x'")]
    [InlineData("='O''Brien'", "f = 'O''Brien'")]
    [InlineData("O'Brien", "f = 'O''Brien'")]
    [InlineData("\"Smith\"", "f = 'Smith'")]
    [InlineData(">date('now')", "f > date('now')")]
    [InlineData("=@id", "f = @id")]
    [InlineData("is null", "f IS NULL")]
    [InlineData("Is Not Null", "f IS NOT NULL")]
    [InlineData("NULL", "f IS NULL")]
    [InlineData("like A%", "f LIKE 'A%'")]
    [InlineData("NOT LIKE '%x'", "f NOT LIKE '%x'")]
    [InlineData("glob a*", "f GLOB 'a*'")]
    [InlineData("in (1, 2, x)", "f IN (1, 2, 'x')")]
    [InlineData("NOT IN ('a,b', c)", "f NOT IN ('a,b', 'c')")]
    [InlineData("IN (SELECT id FROM t)", "f IN (SELECT id FROM t)")]
    [InlineData("between 1 and 10", "f BETWEEN 1 AND 10")]
    [InlineData("NOT BETWEEN 'a' AND 'm'", "f NOT BETWEEN 'a' AND 'm'")]
    [InlineData("=CURRENT_DATE", "f = CURRENT_DATE")]
    [InlineData("=\"t\".\"c\"", "f = \"t\".\"c\"")]
    [InlineData("Linda", "f = 'Linda'")]
    [InlineData("India", "f = 'India'")]
    [InlineData("Island", "f = 'Island'")]
    public void Converts_criteria(string text, string expected) =>
        Assert.Equal(expected, CriteriaParser.ToCondition("f", text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_criteria_give_no_condition(string? text) => Assert.Null(CriteriaParser.ToCondition("f", text));

    [Theory]
    [InlineData(">")]
    [InlineData("between 1")]
    [InlineData("in (1, 2")]
    [InlineData("in (1,,2)")]
    public void Bad_criteria_are_rejected(string text) =>
        Assert.Throws<CriteriaException>(() => CriteriaParser.ToCondition("f", text));
}

public class QuerySqlGeneratorTests
{
    private static QueryDesignDocument OrdersAndCustomers()
    {
        var design = new QueryDesignDocument();
        design.AddTable("customers", 0, 0);
        design.AddTable("orders", 200, 0);
        design.AddJoin("customers", "id", "orders", "customer_id");
        return design;
    }

    private static string Sql(QueryDesignDocument design)
    {
        var result = QuerySqlGenerator.Generate(design);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return result.Sql!;
    }

    [Fact]
    public void Simple_select_quotes_identifiers()
    {
        var design = new QueryDesignDocument();
        design.AddTable("my \"table\"", 0, 0);
        design.Fields.Add(new DesignField { Table = "my \"table\"", Field = "a b" });
        Assert.Equal("SELECT \"my \"\"table\"\"\".\"a b\"\nFROM \"my \"\"table\"\"\";", Sql(design));
    }

    [Fact]
    public void Inner_join_from_join_line()
    {
        var design = OrdersAndCustomers();
        design.Fields.Add(new DesignField { Table = "customers", Field = "name" });
        design.Fields.Add(new DesignField { Table = "orders", Field = "total", Alias = "Order total" });
        Assert.Equal(
            "SELECT \"customers\".\"name\", \"orders\".\"total\" AS \"Order total\"\n" +
            "FROM \"customers\"\nINNER JOIN \"orders\" ON \"customers\".\"id\" = \"orders\".\"customer_id\";",
            Sql(design));
    }

    [Fact]
    public void Left_join_keeps_left_table_and_reversed_direction_becomes_right_join()
    {
        var design = OrdersAndCustomers();
        design.Fields.Add(new DesignField { Table = "customers", Field = "*" });
        design.SetJoinKind(design.Joins[0], JoinKind.Left, keepAllFrom: "customers");
        Assert.Contains("\nLEFT JOIN \"orders\" ON", Sql(design));

        design.SetJoinKind(design.Joins[0], JoinKind.Left, keepAllFrom: "orders");
        Assert.Equal("orders", design.Joins[0].LeftTable);
        Assert.Contains("\nRIGHT JOIN \"orders\" ON \"orders\".\"customer_id\" = \"customers\".\"id\"", Sql(design));
    }

    [Fact]
    public void Cross_join_and_unjoined_tables()
    {
        var design = OrdersAndCustomers();
        design.AddTable("colors", 400, 0);
        design.Fields.Add(new DesignField { Table = "colors", Field = "name" });
        var sql = Sql(design);
        Assert.Contains("INNER JOIN \"orders\"", sql);
        Assert.Contains("\nCROSS JOIN \"colors\";", sql);

        design.SetJoinKind(design.Joins[0], JoinKind.Cross);
        Assert.Contains("\nCROSS JOIN \"orders\"\n", Sql(design));
    }

    [Fact]
    public void Several_join_lines_between_two_tables_are_combined()
    {
        var design = new QueryDesignDocument();
        design.AddTable("a", 0, 0);
        design.AddTable("b", 0, 0);
        design.AddJoin("a", "x", "b", "x");
        design.AddJoin("b", "y", "a", "y"); // Turned to match the first join.
        design.Fields.Add(new DesignField { Table = "a", Field = "*" });
        Assert.Equal("a", design.Joins[1].LeftTable);
        Assert.Contains("INNER JOIN \"b\" ON \"a\".\"x\" = \"b\".\"x\" AND \"a\".\"y\" = \"b\".\"y\"", Sql(design));

        // The same column pair is not added twice.
        design.AddJoin("b", "x", "a", "x");
        Assert.Equal(2, design.Joins.Count);
    }

    [Fact]
    public void Join_order_follows_the_join_lines()
    {
        // c is joined only to b, b only to a: a, then b, then c even though c is listed before b.
        var design = new QueryDesignDocument();
        design.AddTable("a", 0, 0);
        design.AddTable("c", 0, 0);
        design.AddTable("b", 0, 0);
        design.AddJoin("b", "c_id", "c", "id");
        design.AddJoin("a", "id", "b", "a_id");
        design.Fields.Add(new DesignField { Table = "c", Field = "v" });
        Assert.Equal(
            "SELECT \"c\".\"v\"\nFROM \"a\"\nINNER JOIN \"b\" ON \"a\".\"id\" = \"b\".\"a_id\"\nINNER JOIN \"c\" ON \"b\".\"c_id\" = \"c\".\"id\";",
            Sql(design));
    }

    [Fact]
    public void Self_join_uses_aliases()
    {
        var design = new QueryDesignDocument();
        var boss = design.AddTable("employees", 0, 0);
        var worker = design.AddTable("employees", 200, 0);
        Assert.Equal("employees_1", worker.Alias);
        design.AddJoin(boss.Alias, "id", worker.Alias, "manager_id");
        design.Fields.Add(new DesignField { Table = worker.Alias, Field = "name", Alias = "employee" });
        design.Fields.Add(new DesignField { Table = boss.Alias, Field = "name", Alias = "manager" });
        Assert.Equal(
            "SELECT \"employees_1\".\"name\" AS \"employee\", \"employees\".\"name\" AS \"manager\"\n" +
            "FROM \"employees\"\nINNER JOIN \"employees\" AS \"employees_1\" ON \"employees\".\"id\" = \"employees_1\".\"manager_id\";",
            Sql(design));
    }

    [Fact]
    public void Criteria_rows_are_anded_across_fields_and_ored_down()
    {
        var design = OrdersAndCustomers();
        design.Fields.Add(new DesignField { Table = "customers", Field = "country", Criteria = "UK", Or = ["France"] });
        design.Fields.Add(new DesignField { Table = "orders", Field = "total", Criteria = ">100" });
        Assert.EndsWith(
            "\nWHERE (\"customers\".\"country\" = 'UK' AND \"orders\".\"total\" > 100)\n   OR \"customers\".\"country\" = 'France';",
            Sql(design));
    }

    [Fact]
    public void Hidden_fields_still_filter_and_sort()
    {
        var design = OrdersAndCustomers();
        design.Fields.Add(new DesignField { Table = "customers", Field = "name" });
        design.Fields.Add(new DesignField { Table = "orders", Field = "total", Show = false, Criteria = ">0", Sort = SortDirection.Descending });
        Assert.Equal(
            "SELECT \"customers\".\"name\"\n" +
            "FROM \"customers\"\nINNER JOIN \"orders\" ON \"customers\".\"id\" = \"orders\".\"customer_id\"\n" +
            "WHERE \"orders\".\"total\" > 0\nORDER BY \"orders\".\"total\" DESC;",
            Sql(design));
    }

    [Fact]
    public void Sort_order_sets_order_by_position()
    {
        var design = new QueryDesignDocument();
        design.AddTable("t", 0, 0);
        design.Fields.Add(new DesignField { Table = "t", Field = "a", Sort = SortDirection.Ascending });
        design.Fields.Add(new DesignField { Table = "t", Field = "b", Sort = SortDirection.Descending, SortOrder = 1 });
        design.Fields.Add(new DesignField { Table = "t", Field = "c", Sort = SortDirection.Ascending, SortOrder = 2 });
        Assert.EndsWith("ORDER BY \"t\".\"b\" DESC, \"t\".\"c\", \"t\".\"a\";", Sql(design));
    }

    [Fact]
    public void Totals_group_aggregate_where_and_having()
    {
        var design = OrdersAndCustomers();
        design.Totals = true;
        design.Fields.Add(new DesignField { Table = "customers", Field = "country", Total = TotalKind.GroupBy, Criteria = "<>Mars" });
        design.Fields.Add(new DesignField { Table = "orders", Field = "total", Total = TotalKind.Sum, Alias = "sales", Criteria = ">=50", Sort = SortDirection.Descending });
        design.Fields.Add(new DesignField { Table = "orders", Field = "*", Total = TotalKind.Count, Alias = "orders" });
        design.Fields.Add(new DesignField { Table = "orders", Field = "status", Total = TotalKind.Where, Criteria = "paid" });
        Assert.Equal(
            "SELECT \"customers\".\"country\", SUM(\"orders\".\"total\") AS \"sales\", COUNT(*) AS \"orders\"\n" +
            "FROM \"customers\"\nINNER JOIN \"orders\" ON \"customers\".\"id\" = \"orders\".\"customer_id\"\n" +
            "WHERE \"orders\".\"status\" = 'paid'\n" +
            "GROUP BY \"customers\".\"country\"\n" +
            "HAVING \"customers\".\"country\" <> 'Mars' AND SUM(\"orders\".\"total\") >= 50\n" +
            "ORDER BY SUM(\"orders\".\"total\") DESC;",
            Sql(design));
    }

    [Fact]
    public void Expressions_distinct_and_limit()
    {
        var design = new QueryDesignDocument { Distinct = true, Limit = 10 };
        design.AddTable("t", 0, 0);
        design.Fields.Add(new DesignField { Field = "upper(\"t\".\"name\")", Alias = "NAME" });
        Assert.Equal("SELECT DISTINCT upper(\"t\".\"name\") AS \"NAME\"\nFROM \"t\"\nLIMIT 10;", Sql(design));
    }

    [Fact]
    public void Expression_without_tables_is_allowed()
    {
        var design = new QueryDesignDocument();
        design.Fields.Add(new DesignField { Field = "1 + 1", Alias = "two" });
        Assert.Equal("SELECT 1 + 1 AS \"two\";", Sql(design));
    }

    [Fact]
    public void Blank_field_rows_are_ignored() =>
        Assert.Equal("SELECT 1;", Sql(new QueryDesignDocument { Fields = [new DesignField { Field = "  " }, new DesignField { Field = "1" }] }));

    [Theory]
    [InlineData("empty")]
    [InlineData("nothing shown")]
    [InlineData("unknown table")]
    [InlineData("sum star")]
    [InlineData("bad criteria")]
    [InlineData("star criteria")]
    [InlineData("negative limit")]
    public void Problems_are_reported(string problem)
    {
        var design = new QueryDesignDocument();
        if (problem != "empty")
        {
            design.AddTable("t", 0, 0);
        }

        switch (problem)
        {
            case "nothing shown":
                design.Fields.Add(new DesignField { Table = "t", Field = "a", Show = false });
                break;
            case "unknown table":
                design.Fields.Add(new DesignField { Table = "x", Field = "a" });
                break;
            case "sum star":
                design.Totals = true;
                design.Fields.Add(new DesignField { Table = "t", Field = "*", Total = TotalKind.Sum });
                break;
            case "bad criteria":
                design.Fields.Add(new DesignField { Table = "t", Field = "a", Criteria = "between 1" });
                break;
            case "star criteria":
                design.Fields.Add(new DesignField { Table = "t", Field = "*", Criteria = "1" });
                break;
            case "negative limit":
                design.Limit = -1;
                design.Fields.Add(new DesignField { Table = "t", Field = "a" });
                break;
        }

        var result = QuerySqlGenerator.Generate(design);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Removing_a_table_removes_its_joins_and_fields()
    {
        var design = OrdersAndCustomers();
        design.Fields.Add(new DesignField { Table = "orders", Field = "total" });
        design.Fields.Add(new DesignField { Table = "customers", Field = "name" });
        design.RemoveTable("orders");
        Assert.Single(design.Tables);
        Assert.Empty(design.Joins);
        Assert.Equal("name", Assert.Single(design.Fields).Field);
        Assert.Equal("orders", design.NewAlias("orders"));
    }
}

public class QueryDesignDatabaseTests
{
    private static readonly string[] Setup =
    [
        "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, country TEXT)",
        "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers, total REAL, status TEXT)",
        "CREATE TABLE employees (id INTEGER PRIMARY KEY, name TEXT, manager_id INTEGER REFERENCES employees(id))",
        "CREATE TABLE lines (order_id INTEGER, line INTEGER, PRIMARY KEY (order_id, line))",
        "CREATE TABLE notes (id INTEGER PRIMARY KEY, order_id INTEGER, line INTEGER, FOREIGN KEY (order_id, line) REFERENCES lines)",
        "INSERT INTO customers VALUES (1, 'Ann', 'UK'), (2, 'Bob', 'France'), (3, 'Cy', 'UK')",
        "INSERT INTO orders VALUES (1, 1, 100, 'paid'), (2, 1, 50, 'paid'), (3, 2, 70, 'open'), (4, 2, 30, 'paid')",
    ];

    private static async Task<Func<string, TableDetails?>> DetailsAsync(TestDatabase db)
    {
        var schema = new SchemaService(db.Session);
        var cache = new Dictionary<string, TableDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "customers", "orders", "employees", "lines", "notes" })
        {
            cache[name] = await schema.GetTableDetailsAsync(name);
        }

        return name => cache.GetValueOrDefault(name);
    }

    [Fact]
    public async Task Suggests_joins_from_foreign_keys_in_both_directions()
    {
        await using var db = await TestDatabase.CreateAsync(Setup);
        var details = await DetailsAsync(db);

        var design = new QueryDesignDocument();
        design.AddTable("orders", 0, 0);
        var customers = design.AddTable("customers", 0, 0);
        var join = Assert.Single(JoinSuggester.Suggest(design, customers, details));
        Assert.Equal(("customers", "id", "orders", "customer_id"), (join.LeftTable, join.LeftColumn, join.RightTable, join.RightColumn));

        // Composite key without target columns: uses the referenced primary key.
        var lines = new QueryDesignDocument();
        lines.AddTable("notes", 0, 0);
        var added = lines.AddTable("lines", 0, 0);
        var joins = JoinSuggester.Suggest(lines, added, details);
        Assert.Equal(["order_id", "line"], joins.Select(j => j.LeftColumn));
        Assert.Equal(["order_id", "line"], joins.Select(j => j.RightColumn));

        // Joins already drawn are not suggested again.
        design.Joins.Add(join);
        Assert.Empty(JoinSuggester.Suggest(design, customers, details));
    }

    [Fact]
    public async Task Suggests_self_join_for_second_copy()
    {
        await using var db = await TestDatabase.CreateAsync(Setup);
        var design = new QueryDesignDocument();
        design.AddTable("employees", 0, 0);
        var copy = design.AddTable("employees", 0, 0);
        var join = Assert.Single(JoinSuggester.Suggest(design, copy, await DetailsAsync(db)));
        Assert.Equal(("employees", "id", "employees_1", "manager_id"), (join.LeftTable, join.LeftColumn, join.RightTable, join.RightColumn));
    }

    [Fact]
    public async Task Generated_queries_run_and_return_the_expected_rows()
    {
        await using var db = await TestDatabase.CreateAsync(Setup);

        var design = new QueryDesignDocument { Totals = true };
        design.AddTable("customers", 0, 0);
        design.AddTable("orders", 0, 0);
        design.AddJoin("customers", "id", "orders", "customer_id");
        design.SetJoinKind(design.Joins[0], JoinKind.Left, "customers");
        design.Fields.Add(new DesignField { Table = "customers", Field = "name", Total = TotalKind.GroupBy, Sort = SortDirection.Ascending });
        design.Fields.Add(new DesignField { Table = "orders", Field = "total", Total = TotalKind.Sum, Alias = "sales" });
        var rows = await db.Session.QueryRowsAsync(QuerySqlGenerator.Generate(design).Sql!);
        Assert.Equal(["Ann", "Bob", "Cy"], rows.Rows.Select(r => (string)r[0]!));
        Assert.Equal(new object?[] { 150.0, 100.0, null }, rows.Rows.Select(r => r[1]));

        // Criteria with an OR row and the paid filter in WHERE.
        design.Fields.Add(new DesignField { Table = "orders", Field = "status", Total = TotalKind.Where, Criteria = "paid", Or = ["paid"] });
        design.Fields[1].Criteria = ">=100";
        design.Fields[0].Or = ["Bob"];
        rows = await db.Session.QueryRowsAsync(QuerySqlGenerator.Generate(design).Sql!);
        Assert.Equal(["Ann", "Bob"], rows.Rows.Select(r => (string)r[0]!));

        // Right join (all orders) also runs.
        design = new QueryDesignDocument();
        design.AddTable("customers", 0, 0);
        design.AddTable("orders", 0, 0);
        design.AddJoin("customers", "id", "orders", "customer_id", JoinKind.Left);
        design.SetJoinKind(design.Joins[0], JoinKind.Left, "orders");
        design.Fields.Add(new DesignField { Table = "orders", Field = "id" });
        rows = await db.Session.QueryRowsAsync(QuerySqlGenerator.Generate(design).Sql!);
        Assert.Equal(4, rows.Rows.Count);
    }

    [Fact]
    public async Task Design_round_trips_through_a_file()
    {
        var design = new QueryDesignDocument { Database = @"C:\data\shop.db", Totals = true, Distinct = true, Limit = 5 };
        design.AddTable("customers", 10, 20);
        design.AddTable("orders", 250, 40);
        design.AddJoin("customers", "id", "orders", "customer_id", JoinKind.Left);
        design.Fields.Add(new DesignField { Table = "customers", Field = "name", Alias = "Name", Total = TotalKind.GroupBy, Sort = SortDirection.Descending, SortOrder = 2, Criteria = "like A%", Or = [null, "Bob"] });
        design.Fields.Add(new DesignField { Field = "count(*)", Show = false, Total = TotalKind.Expression });

        var folder = TestDatabase.NewFolder();
        try
        {
            var path = Path.Combine(folder, "q" + QueryDesignSerializer.FileExtension);
            await QueryDesignSerializer.SaveAsync(path, design);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("\"kind\": \"left\"", text);
            Assert.Contains("\"total\": \"groupBy\"", text);

            var loaded = await QueryDesignSerializer.LoadAsync(path);
            Assert.Equal(QueryDesignSerializer.Serialize(design), QueryDesignSerializer.Serialize(loaded));
            Assert.Equal(QuerySqlGenerator.Generate(design).Sql, QuerySqlGenerator.Generate(loaded).Sql);
            Assert.Equal(250, loaded.Tables[1].X);
            Assert.Equal([null, "Bob"], loaded.Fields[0].Or);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"version\": 99}")]
    public void Bad_design_files_are_rejected(string json) =>
        Assert.ThrowsAny<JsonException>(() => QueryDesignSerializer.Deserialize(json));

    [Fact]
    public void Missing_lists_become_empty()
    {
        var design = QueryDesignSerializer.Deserialize("{\"tables\": null, \"fields\": [{\"field\": \"1\", \"or\": null}]}");
        Assert.Empty(design.Tables);
        Assert.Empty(design.Joins);
        Assert.Empty(design.Fields[0].Or);
    }
}
