using SqliteManager.Core.Design;

namespace SqliteManager.Core.Tests;

public class DesignValidatorTests
{
    private static TableDefinition Table(params ColumnDefinition[] columns) => new() { Name = "t", Columns = [.. columns] };

    private static ColumnDefinition Col(string name, string type = "TEXT") => new() { Name = name, Type = type };

    private static string[] Errors(TableDefinition design, TableDefinition? original = null, long rows = 0, params string[] others) =>
        DesignValidator.Validate(design, original, rows, others).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToArray();

    [Fact]
    public void A_simple_table_is_valid()
    {
        Assert.Empty(Errors(Table(new ColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKeyOrder = 1, AutoIncrement = true }, Col("name"))));
    }

    [Fact]
    public void Names_are_required_unique_and_not_reserved()
    {
        Assert.Contains("Enter a table name.", Errors(new TableDefinition { Columns = [Col("a")] }));
        Assert.Contains(Errors(new TableDefinition { Name = "sqlite_x", Columns = [Col("a")] }), e => e.Contains("reserved", StringComparison.Ordinal));
        Assert.Contains(Errors(Table(Col("a")), others: "T"), e => e.Contains("already exists", StringComparison.Ordinal));
        Assert.Contains("Add at least one column.", Errors(Table()));
        Assert.Contains(Errors(Table(Col("a"), Col("A"))), e => e.Contains("more than once", StringComparison.Ordinal));
        Assert.Contains(Errors(Table(Col(""))), e => e.Contains("has no name", StringComparison.Ordinal));
    }

    [Fact]
    public void Autoincrement_rules()
    {
        Assert.NotEmpty(Errors(Table(new ColumnDefinition { Name = "id", Type = "INT", PrimaryKeyOrder = 1, AutoIncrement = true })));
        Assert.NotEmpty(Errors(Table(new ColumnDefinition { Name = "id", Type = "INTEGER", AutoIncrement = true })));
        Assert.NotEmpty(Errors(Table(
            new ColumnDefinition { Name = "a", Type = "INTEGER", PrimaryKeyOrder = 1, AutoIncrement = true },
            new ColumnDefinition { Name = "b", Type = "INTEGER", PrimaryKeyOrder = 2 })));
    }

    [Fact]
    public void Without_rowid_and_strict_rules()
    {
        var noPk = Table(Col("a"));
        noPk.WithoutRowId = true;
        Assert.Contains("A WITHOUT ROWID table needs a primary key.", Errors(noPk));

        var strict = Table(Col("a", "VARCHAR(10)"), Col("b", "integer"), Col("c", ""));
        strict.Strict = true;
        Assert.Equal(2, Errors(strict).Length);
    }

    [Theory]
    [InlineData("'text'", true)]
    [InlineData("42", true)]
    [InlineData("-1.5", true)]
    [InlineData("NULL", true)]
    [InlineData("current_timestamp", true)]
    [InlineData("x'00'", true)]
    [InlineData("(datetime('now'))", true)]
    [InlineData("hello", false)]
    [InlineData("1 + 2", false)]
    [InlineData("(1) + (2)", false)]
    [InlineData("'open", false)]
    [InlineData("(1", false)]
    public void Default_values(string text, bool valid)
    {
        Assert.Equal(valid, DesignValidator.IsValidDefault(text));
    }

    [Fact]
    public void New_not_null_column_needs_a_default_when_rows_exist()
    {
        var original = Table(new ColumnDefinition { Name = "a", OriginalName = "a" });
        var design = original.Clone();
        design.Columns.Add(new ColumnDefinition { Name = "b", NotNull = true });

        Assert.Contains(Errors(design, original, rows: 3), e => e.Contains("has no default", StringComparison.Ordinal));
        Assert.Empty(Errors(design, original, rows: 0));
        design.Columns[1].Default = "0";
        Assert.Empty(Errors(design, original, rows: 3));
    }

    [Fact]
    public void Index_and_foreign_key_rules()
    {
        var design = Table(Col("a"), Col("b"));
        design.Indexes.Add(new IndexDefinition { Name = "ix", Terms = [IndexTerm.ForColumn("zz")] });
        design.Indexes.Add(new IndexDefinition { Name = "IX", Terms = [] });
        design.ForeignKeys.Add(new ForeignKeyDefinition { Columns = ["a", "b"], Table = "p", ReferencedColumns = ["x"] });
        design.ForeignKeys.Add(new ForeignKeyDefinition { Columns = ["q"], Table = "" });

        var errors = Errors(design);

        Assert.Contains(errors, e => e.Contains("unknown column \"zz\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("used more than once", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("has no columns", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("references 1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no referenced table", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown column \"q\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Unbalanced_expressions_are_errors()
    {
        var design = Table(new ColumnDefinition { Name = "a", Check = "a > (1" });

        Assert.Contains(Errors(design), e => e.Contains("unbalanced", StringComparison.Ordinal));
    }

    [Fact]
    public void Rename_column_updates_indexes_and_foreign_keys()
    {
        var design = Table(Col("a"), Col("b"));
        design.Indexes.Add(new IndexDefinition { Name = "ix", Terms = [IndexTerm.ForColumn("a", true), new IndexTerm(null, "lower(a)")] });
        design.ForeignKeys.Add(new ForeignKeyDefinition { Columns = ["a"], Table = "p" });

        design.RenameColumn(design.Columns[0], "z");

        Assert.Equal(IndexTerm.ForColumn("z", true), design.Indexes[0].Terms[0]);
        Assert.Equal("lower(\"z\")", design.Indexes[0].Terms[1].Expression);
        Assert.Equal(["z"], design.ForeignKeys[0].Columns);
    }
}
