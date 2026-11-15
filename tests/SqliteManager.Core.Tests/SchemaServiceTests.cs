using SqliteManager.Core.Schema;

namespace SqliteManager.Core.Tests;

public class SchemaServiceTests
{
    private static readonly string[] SampleSchema =
    [
        "CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL UNIQUE, name TEXT DEFAULT 'n/a')",
        "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id) ON DELETE CASCADE, " +
            "total REAL, created TEXT)",
        "CREATE TABLE order_lines (order_id INTEGER, line_no INTEGER, qty INTEGER, PRIMARY KEY (order_id, line_no), " +
            "FOREIGN KEY (order_id) REFERENCES orders(id)) WITHOUT ROWID",
        "CREATE TABLE strict_t (a INTEGER, b TEXT, c INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL) STRICT",
        "CREATE INDEX ix_orders_customer_created ON orders (customer_id, created)",
        "CREATE INDEX ix_orders_big ON orders (total) WHERE total > 1000",
        "CREATE VIEW v_customer_orders AS SELECT c.name, o.total FROM customers c JOIN orders o ON o.customer_id = c.id",
        "CREATE TRIGGER trg_orders_ai AFTER INSERT ON orders BEGIN UPDATE customers SET name = name WHERE id = NEW.customer_id; END",
    ];

    [Fact]
    public async Task GetObjects_lists_tables_views_indexes_and_triggers()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);
        var schema = new SchemaService(db.Session);

        var objects = await schema.GetObjectsAsync();

        string[] Names(SchemaObjectType type) => objects.Where(o => o.Type == type).Select(o => o.Name).ToArray();
        Assert.Equal(["customers", "order_lines", "orders", "sqlite_sequence", "strict_t"], Names(SchemaObjectType.Table));
        Assert.Equal(["v_customer_orders"], Names(SchemaObjectType.View));
        Assert.Equal(["trg_orders_ai"], Names(SchemaObjectType.Trigger));
        Assert.Contains("ix_orders_customer_created", Names(SchemaObjectType.Index));
        Assert.Contains("ix_orders_big", Names(SchemaObjectType.Index));

        var trigger = objects.Single(o => o.Type == SchemaObjectType.Trigger);
        Assert.Equal("orders", trigger.TableName);
        Assert.StartsWith("CREATE TRIGGER", trigger.Sql);

        Assert.True(objects.Single(o => o.Name == "sqlite_sequence").IsSystem);
        Assert.False(objects.Single(o => o.Name == "customers").IsSystem);
        var autoIndex = objects.Single(o => o.Type == SchemaObjectType.Index && o.TableName == "customers");
        Assert.True(autoIndex.IsSystem);
        Assert.Null(autoIndex.Sql);
    }

    [Fact]
    public async Task GetObjects_on_an_empty_database_returns_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.Empty(await new SchemaService(db.Session).GetObjectsAsync());
    }

    [Fact]
    public async Task GetTableDetails_reads_columns()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("customers");

        Assert.Equal(TableKind.Table, details.Kind);
        Assert.True(details.HasRowId);
        Assert.False(details.IsStrict);
        Assert.Equal(["id", "email", "name"], details.Columns.Select(c => c.Name));

        var id = details.Columns[0];
        Assert.Equal("INTEGER", id.DeclaredType);
        Assert.True(id.IsPrimaryKey);

        var email = details.Columns[1];
        Assert.True(email.NotNull);
        Assert.False(email.IsPrimaryKey);

        Assert.Equal("'n/a'", details.Columns[2].DefaultValue);
        Assert.Equal(["id"], details.PrimaryKey.Select(c => c.Name));
    }

    [Fact]
    public async Task GetTableDetails_reads_composite_primary_key_and_without_rowid()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("order_lines");

        Assert.False(details.HasRowId);
        Assert.Equal(["order_id", "line_no"], details.PrimaryKey.Select(c => c.Name));
    }

    [Fact]
    public async Task GetTableDetails_reads_foreign_keys()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("orders");

        var fk = Assert.Single(details.ForeignKeys);
        Assert.Equal("customers", fk.ReferencedTable);
        Assert.Equal(["customer_id"], fk.FromColumns);
        Assert.Equal(["id"], fk.ToColumns);
        Assert.Equal("CASCADE", fk.OnDelete);
        Assert.Equal("NO ACTION", fk.OnUpdate);
    }

    [Fact]
    public async Task GetTableDetails_reads_indexes()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);
        var schema = new SchemaService(db.Session);

        var orders = await schema.GetTableDetailsAsync("orders");
        var composite = orders.Indexes.Single(i => i.Name == "ix_orders_customer_created");
        Assert.Equal(["customer_id", "created"], composite.Columns);
        Assert.False(composite.IsUnique);
        Assert.Equal("c", composite.Origin);
        Assert.False(composite.IsPartial);
        Assert.True(orders.Indexes.Single(i => i.Name == "ix_orders_big").IsPartial);

        var customers = await schema.GetTableDetailsAsync("customers");
        var unique = Assert.Single(customers.Indexes);
        Assert.True(unique.IsUnique);
        Assert.Equal("u", unique.Origin);
        Assert.Equal(["email"], unique.Columns);
    }

    [Fact]
    public async Task GetTableDetails_reads_strict_and_generated_columns()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("strict_t");

        Assert.True(details.IsStrict);
        Assert.Equal(["a", "b", "c"], details.Columns.Select(c => c.Name));
        Assert.True(details.Columns[2].IsGenerated);
        Assert.False(details.Columns[0].IsGenerated);
    }

    [Fact]
    public async Task GetTableDetails_works_for_views()
    {
        await using var db = await TestDatabase.CreateAsync(SampleSchema);

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("v_customer_orders");

        Assert.Equal(TableKind.View, details.Kind);
        Assert.False(details.HasRowId);
        Assert.Equal(["name", "total"], details.Columns.Select(c => c.Name));
        Assert.Empty(details.ForeignKeys);
        Assert.Empty(details.Indexes);
    }

    [Fact]
    public async Task GetTableDetails_handles_names_that_need_quoting()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE \"my \"\"odd\"\" table\" (\"col one\" TEXT)",
            "CREATE INDEX \"ix \"\"odd\"\"\" ON \"my \"\"odd\"\" table\" (\"col one\")");

        var details = await new SchemaService(db.Session).GetTableDetailsAsync("my \"odd\" table");

        Assert.Equal(["col one"], details.Columns.Select(c => c.Name));
        Assert.Equal(["col one"], Assert.Single(details.Indexes).Columns);
    }

    [Fact]
    public async Task GetTableDetails_throws_for_unknown_table()
    {
        await using var db = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => new SchemaService(db.Session).GetTableDetailsAsync("nope"));
    }
}
