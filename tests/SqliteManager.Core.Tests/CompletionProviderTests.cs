using SqliteManager.Core.Data;
using SqliteManager.Core.Query;
using SqliteManager.Core.Schema;

namespace SqliteManager.Core.Tests;

public class CompletionProviderTests
{
    private static readonly SchemaSnapshot Schema = new(
        Tables: ["customers", "orders", "order lines"],
        Views: ["v_totals"],
        Columns: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["customers"] = ["id", "name", "email"],
            ["orders"] = ["id", "customer_id", "total", "order"],
            ["order lines"] = ["order_id", "qty"],
            ["v_totals"] = ["customer", "sum_total"],
        },
        Functions: ["count", "coalesce", "upper"]);

    /// <summary>Completes at the position marked with |.</summary>
    private static CompletionResult At(string textWithCaret, bool explicitRequest = false)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return CompletionProvider.Complete(textWithCaret.Remove(caret, 1), caret, Schema, explicitRequest);
    }

    private static string[] Texts(CompletionResult result) => result.Items.Select(i => i.Text).ToArray();

    [Fact]
    public void Prefix_matches_keywords_tables_views_and_functions()
    {
        var result = At("SELECT * FROM c|");

        Assert.Equal("c", result.Prefix);
        Assert.Equal(14, result.WordStart);
        Assert.Contains(result.Items, i => i is { Text: "customers", Kind: CompletionKind.Table });
        Assert.Contains(result.Items, i => i is { Text: "count", Kind: CompletionKind.Function });
        Assert.Contains(result.Items, i => i is { Text: "CREATE", Kind: CompletionKind.Keyword });
        Assert.All(result.Items, i => Assert.StartsWith("c", i.Text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Items, i => i.Text == "orders");
    }

    [Fact]
    public void Matching_ignores_case_and_results_are_sorted()
    {
        var texts = Texts(At("sel|"));

        Assert.Equal(["SELECT"], texts);
        var all = Texts(At("SELECT * FROM o|"));
        Assert.Equal(all.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), all);
    }

    [Fact]
    public void Nothing_typed_gives_nothing_unless_requested()
    {
        Assert.Empty(At("SELECT |").Items);
        Assert.NotEmpty(At("SELECT |", explicitRequest: true).Items);
    }

    [Fact]
    public void Table_dot_lists_that_tables_columns()
    {
        var result = At("SELECT customers.| FROM customers");

        Assert.Equal(["email", "id", "name"], Texts(result));
        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
    }

    [Theory]
    [InlineData("SELECT o.| FROM orders o")]
    [InlineData("SELECT o.| FROM orders AS o")]
    [InlineData("SELECT o.| FROM customers c JOIN orders o ON o.customer_id = c.id")]
    [InlineData("SELECT o.| FROM customers c, orders o")]
    [InlineData("SELECT o.| FROM main.orders o")]
    [InlineData("SELECT O.| FROM Orders o")]
    [InlineData("SELECT x FROM t; SELECT o.| FROM orders o; SELECT 1")]
    public void Alias_dot_lists_the_aliased_tables_columns(string text)
    {
        Assert.Equal(["customer_id", "id", "order", "total"], Texts(At(text)));
    }

    [Fact]
    public void Column_prefix_after_alias_filters()
    {
        var result = At("SELECT c.na| FROM customers c");

        Assert.Equal(["name"], Texts(result));
        Assert.Equal("na", result.Prefix);
    }

    [Fact]
    public void Quoted_names_resolve_and_are_quoted_on_insert()
    {
        var result = At("SELECT l.| FROM \"order lines\" l");
        Assert.Equal(["order_id", "qty"], Texts(result));

        var direct = At("SELECT \"order lines\".| FROM \"order lines\"");
        Assert.Equal(["order_id", "qty"], Texts(direct));

        var keywordColumn = At("SELECT o.ord| FROM orders o").Items.Single();
        Assert.Equal("\"order\"", keywordColumn.InsertText);

        var spacedTable = At("SELECT * FROM order|").Items.Single(i => i.Text == "order lines");
        Assert.Equal("\"order lines\"", spacedTable.InsertText);
    }

    [Fact]
    public void Views_resolve_like_tables()
    {
        Assert.Equal(["customer", "sum_total"], Texts(At("SELECT v.| FROM v_totals v")));
    }

    [Fact]
    public void Unknown_alias_gives_no_columns()
    {
        Assert.Empty(At("SELECT z.| FROM orders o").Items);
    }

    [Theory]
    [InlineData("SELECT 'cu|'")]
    [InlineData("SELECT 1 -- cu|")]
    [InlineData("SELECT /* o.| */ 1 FROM orders o")]
    public void No_suggestions_inside_strings_or_comments(string text)
    {
        Assert.Empty(At(text).Items);
    }

    [Fact]
    public void FindTableAliases_does_not_treat_keywords_as_aliases()
    {
        var aliases = CompletionProvider.FindTableAliases(
            "SELECT * FROM orders WHERE id IN (SELECT id FROM customers c) ORDER BY 1", Schema);

        Assert.Equal("orders", aliases["orders"]);
        Assert.Equal("customers", aliases["c"]);
        Assert.False(aliases.ContainsKey("WHERE"));
        Assert.False(aliases.ContainsKey("ORDER"));
    }

    [Fact]
    public async Task Works_with_a_live_schema_snapshot()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE people (id INTEGER PRIMARY KEY, full_name TEXT)",
            "CREATE VIEW v_people AS SELECT full_name FROM people");

        var snapshot = await new SchemaService(db.Session).GetSnapshotAsync();
        var result = CompletionProvider.Complete("SELECT p. FROM people p", 9, snapshot);

        Assert.Equal(["people"], snapshot.Tables);
        Assert.Equal(["v_people"], snapshot.Views);
        Assert.Equal(["full_name"], snapshot.Columns["V_PEOPLE"]);
        Assert.Contains("upper", snapshot.Functions);
        Assert.Equal(["full_name", "id"], result.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task Snapshot_survives_a_broken_view()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE a (x)",
            "CREATE TABLE b (y)",
            "CREATE VIEW v AS SELECT y FROM b",
            "DROP TABLE b");

        var snapshot = await new SchemaService(db.Session).GetSnapshotAsync();

        Assert.Equal(["x"], snapshot.Columns["a"]);
        Assert.Empty(snapshot.Columns["v"]);
    }

    // ---- Attached databases -------------------------------------------------------------------

    private static readonly SchemaSnapshot WithAttached = Schema with
    {
        Attached =
        [
            new AttachedSchema(
                "aux",
                Tables: ["invoices", "customers"],
                Views: ["v_open"],
                Columns: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["invoices"] = ["invoice_no", "amount"],
                    ["customers"] = ["cust_code", "region"],
                    ["v_open"] = ["invoice_no"],
                }),
            new AttachedSchema(
                "my archive",
                Tables: ["old_orders"],
                Views: [],
                Columns: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["old_orders"] = ["archived_at"],
                }),
        ],
    };

    private static CompletionResult AtAttached(string textWithCaret, bool explicitRequest = false)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return CompletionProvider.Complete(textWithCaret.Remove(caret, 1), caret, WithAttached, explicitRequest);
    }

    [Fact]
    public void Schema_dot_lists_tables_and_views_of_the_attached_database()
    {
        var result = AtAttached("SELECT * FROM aux.|");

        Assert.Equal(["customers", "invoices", "v_open"], Texts(result));
        Assert.Contains(result.Items, i => i is { Text: "invoices", Kind: CompletionKind.Table });
        Assert.Contains(result.Items, i => i is { Text: "v_open", Kind: CompletionKind.View });
        Assert.Equal(["invoices"], Texts(AtAttached("SELECT * FROM AUX.in|")));
    }

    [Fact]
    public void Quoted_schema_names_work_and_main_lists_main_tables()
    {
        Assert.Equal(["old_orders"], Texts(AtAttached("SELECT * FROM \"my archive\".|")));
        Assert.Equal(["customers", "order lines", "orders", "v_totals"], Texts(AtAttached("SELECT * FROM main.|")));
    }

    [Fact]
    public void Attached_schema_names_are_suggested_with_quoting_when_needed()
    {
        var aux = Assert.Single(AtAttached("SELECT * FROM au|").Items, i => i.Kind == CompletionKind.Schema);
        Assert.Equal("aux", aux.InsertText);

        var archive = Assert.Single(AtAttached("SELECT * FROM my|").Items, i => i.Kind == CompletionKind.Schema);
        Assert.Equal("\"my archive\"", archive.InsertText);

        Assert.DoesNotContain(At("SELECT * FROM au|").Items, i => i.Kind == CompletionKind.Schema);
    }

    [Fact]
    public void Alias_of_an_attached_table_lists_its_columns()
    {
        Assert.Equal(["amount", "invoice_no"], Texts(AtAttached("SELECT i.| FROM aux.invoices AS i")));
        Assert.Equal(["region"], Texts(AtAttached("SELECT c.r| FROM aux.customers c JOIN orders o ON o.customer_id = c.cust_code")));
    }

    [Fact]
    public void Schema_qualified_table_in_FROM_uses_that_schema_even_when_main_has_the_same_name()
    {
        // main also has a customers table (id, name, email).
        Assert.Equal(["cust_code", "region"], Texts(AtAttached("SELECT c.| FROM aux.customers c")));
        Assert.Equal(["email", "id", "name"], Texts(AtAttached("SELECT c.| FROM customers c")));
        Assert.Equal(["email", "id", "name"], Texts(AtAttached("SELECT c.| FROM main.customers c")));
    }

    [Fact]
    public void Schema_table_dot_lists_columns()
    {
        Assert.Equal(["cust_code", "region"], Texts(AtAttached("SELECT aux.customers.| FROM aux.customers")));
        Assert.Equal(["archived_at"], Texts(AtAttached("SELECT \"my archive\".old_orders.| FROM \"my archive\".old_orders")));
        Assert.Empty(AtAttached("SELECT aux.nope.| FROM aux.nope").Items);
    }

    [Fact]
    public void Unqualified_table_found_only_in_an_attached_database_resolves_there()
    {
        Assert.Equal(["amount", "invoice_no"], Texts(AtAttached("SELECT invoices.| FROM invoices")));
        Assert.Equal(["archived_at"], Texts(AtAttached("SELECT x.| FROM old_orders x")));
    }

    [Fact]
    public void FindTableAliases_keeps_table_names_for_schema_qualified_sources()
    {
        var aliases = CompletionProvider.FindTableAliases("SELECT * FROM aux.invoices i JOIN orders o ON 1", WithAttached);

        Assert.Equal("invoices", aliases["i"]);
        Assert.Equal("orders", aliases["o"]);
    }

    [Fact]
    public async Task Live_snapshot_includes_attached_databases()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE local_t (id INTEGER)");
        var otherPath = Path.Combine(db.Folder, "other.db");
        var other = await db.Manager.CreateAsync(otherPath);
        await other.ExecuteNonQueryAsync("CREATE TABLE remote_t (code TEXT, qty INTEGER)");
        await other.ExecuteNonQueryAsync("CREATE VIEW remote_v AS SELECT code FROM remote_t");
        await other.ExecuteNonQueryAsync("CREATE TABLE local_t (different_column TEXT)");
        await db.Manager.CloseAsync(other);
        await new AttachService(db.Session).AttachAsync(otherPath, "my db");

        var snapshot = await new SchemaService(db.Session).GetSnapshotAsync();

        Assert.Equal(["local_t"], snapshot.Tables);
        Assert.Equal(["id"], snapshot.Columns["local_t"]);
        var attached = Assert.Single(snapshot.AttachedSchemas);
        Assert.Equal("my db", attached.Name);
        Assert.Equal(["local_t", "remote_t"], attached.Tables);
        Assert.Equal(["remote_v"], attached.Views);
        Assert.Equal(["code", "qty"], attached.Columns["remote_t"]);
        Assert.Equal(["different_column"], attached.Columns["LOCAL_T"]);
        Assert.Equal(["code"], attached.Columns["remote_v"]);

        var result = CompletionProvider.Complete("SELECT * FROM \"my db\".", 22, snapshot);
        Assert.Equal(["local_t", "remote_t", "remote_v"], Texts(result));

        await new AttachService(db.Session).DetachAsync("my db");
        Assert.Empty((await new SchemaService(db.Session).GetSnapshotAsync()).AttachedSchemas);
    }
}
