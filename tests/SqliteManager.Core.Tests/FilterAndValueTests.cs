using SqliteManager.Core.Data;

namespace SqliteManager.Core.Tests;

public class FilterAndValueTests
{
    private static readonly string[] Columns = ["name", "age", "my col"];

    private static FilterCondition Build(params (string Column, string Text)[] filters) =>
        FilterParser.Build(filters.Select(f => new ColumnFilter(f.Column, f.Text)), Columns);

    [Fact]
    public void Plain_text_means_contains()
    {
        var condition = Build(("name", "ann"));

        Assert.Equal("\"name\" LIKE @f0 ESCAPE '\\'", condition.Sql);
        Assert.Equal("%ann%", condition.Parameters["@f0"]);
    }

    [Fact]
    public void Like_wildcards_in_text_are_escaped()
    {
        Assert.Equal("%50\\%\\_off\\\\%", Build(("name", "50%_off\\")).Parameters["@f0"]);
    }

    [Theory]
    [InlineData("=5", "\"age\" = @f0", 5L)]
    [InlineData("<> 5", "\"age\" <> @f0", 5L)]
    [InlineData("!=5", "\"age\" <> @f0", 5L)]
    [InlineData(">=2.5", "\"age\" >= @f0", 2.5)]
    [InlineData("<=-1", "\"age\" <= @f0", -1L)]
    [InlineData(">x", "\"age\" > @f0", "x")]
    [InlineData("<10", "\"age\" < @f0", 10L)]
    [InlineData("=", "\"age\" = @f0", "")]
    public void Operators(string text, string sql, object value)
    {
        var condition = Build(("age", text));

        Assert.Equal(sql, condition.Sql);
        Assert.Equal(value, condition.Parameters["@f0"]);
    }

    [Theory]
    [InlineData("is null", "\"age\" IS NULL")]
    [InlineData(" IS NOT NULL ", "\"age\" IS NOT NULL")]
    public void Null_checks(string text, string sql)
    {
        var condition = Build(("age", text));

        Assert.Equal(sql, condition.Sql);
        Assert.Empty(condition.Parameters);
    }

    [Fact]
    public void Filters_are_joined_with_and_and_blank_or_unknown_ones_are_ignored()
    {
        var condition = Build(("name", "a"), ("age", "  "), ("missing", "x"), ("MY COL", ">1"));

        Assert.Equal("\"name\" LIKE @f0 ESCAPE '\\' AND \"my col\" > @f1", condition.Sql);
        Assert.True(Build(("age", "")).IsEmpty);
    }

    [Fact]
    public async Task Filters_select_the_right_rows()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE p (name TEXT, age INTEGER, \"my col\" TEXT)",
            "INSERT INTO p VALUES ('Ann', 30, 'x'), ('anna', 5, NULL), ('Bob', 40, 'y'), ('50% off', 100, 'z'), ('b_c', NULL, '')");
        var service = new TableDataService(db.Session);

        async Task<string[]> NamesAsync(params (string, string)[] filters)
        {
            var snapshot = await service.OpenAsync("p", new TableQuery(filters.Select(f => new ColumnFilter(f.Item1, f.Item2)).ToList()));
            return (await service.GetPageAsync(snapshot, 0, 100)).Select(r => (string)r.Values[0]!).ToArray();
        }

        Assert.Equal(["Ann", "anna"], await NamesAsync(("name", "ANN")));
        Assert.Equal(["Ann", "Bob"], await NamesAsync(("age", ">=30"), ("age", "<100")));
        Assert.Equal(["50% off"], await NamesAsync(("name", "%")));
        Assert.Equal(["b_c"], await NamesAsync(("name", "_")));
        Assert.Equal(["anna"], await NamesAsync(("my col", "IS NULL")));
        Assert.Equal(["b_c"], await NamesAsync(("my col", "=")));
        Assert.Equal(["Ann", "anna", "Bob", "50% off"], await NamesAsync(("age", "is not null")));
        Assert.Equal(["Bob", "50% off"], await NamesAsync(("age", ">30")));
    }

    [Theory]
    [InlineData("INTEGER", ColumnAffinity.Integer)]
    [InlineData("BIGINT", ColumnAffinity.Integer)]
    [InlineData("VARCHAR(20)", ColumnAffinity.Text)]
    [InlineData("nchar", ColumnAffinity.Text)]
    [InlineData("CLOB", ColumnAffinity.Text)]
    [InlineData("TEXT", ColumnAffinity.Text)]
    [InlineData("BLOB", ColumnAffinity.Blob)]
    [InlineData("", ColumnAffinity.Blob)]
    [InlineData(null, ColumnAffinity.Blob)]
    [InlineData("ANY", ColumnAffinity.Blob)]
    [InlineData("REAL", ColumnAffinity.Real)]
    [InlineData("DOUBLE PRECISION", ColumnAffinity.Real)]
    [InlineData("FLOAT", ColumnAffinity.Real)]
    [InlineData("NUMERIC", ColumnAffinity.Numeric)]
    [InlineData("DECIMAL(10,5)", ColumnAffinity.Numeric)]
    [InlineData("DATETIME", ColumnAffinity.Numeric)]
    [InlineData("CHARINT", ColumnAffinity.Integer)] // INT is checked first.
    public void Affinity_rules(string? declared, ColumnAffinity expected)
    {
        Assert.Equal(expected, ValueParser.GetAffinity(declared));
    }

    [Theory]
    [InlineData("42", ColumnAffinity.Integer, 42L)]
    [InlineData("-7", ColumnAffinity.Numeric, -7L)]
    [InlineData("3.0", ColumnAffinity.Integer, 3L)]
    [InlineData("3.5", ColumnAffinity.Integer, 3.5)]
    [InlineData("1e3", ColumnAffinity.Numeric, 1000L)]
    [InlineData("42", ColumnAffinity.Real, 42.0)]
    [InlineData("2.5", ColumnAffinity.Real, 2.5)]
    [InlineData("42", ColumnAffinity.Text, "42")]
    [InlineData("007", ColumnAffinity.Text, "007")]
    [InlineData("42", ColumnAffinity.Blob, 42L)]
    [InlineData("1,234", ColumnAffinity.Integer, "1,234")]
    [InlineData(" 42", ColumnAffinity.Integer, " 42")]
    [InlineData("abc", ColumnAffinity.Integer, "abc")]
    [InlineData("", ColumnAffinity.Integer, "")]
    [InlineData("NaN", ColumnAffinity.Real, "NaN")]
    public void Parse(string text, ColumnAffinity affinity, object expected)
    {
        Assert.Equal(expected, ValueParser.Parse(text, affinity));
    }

    [Fact]
    public void Very_large_integers_become_real()
    {
        Assert.Equal(1e20, ValueParser.Parse("100000000000000000000", ColumnAffinity.Integer));
    }

    [Fact]
    public void FormatForEdit_uses_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal("1234.5", ValueParser.FormatForEdit(1234.5));
            Assert.Equal("1234567", ValueParser.FormatForEdit(1234567L));
            Assert.Equal("0.1", ValueParser.FormatForEdit(0.1));
            Assert.Equal(string.Empty, ValueParser.FormatForEdit(null));
            Assert.Equal("CAFE", ValueParser.FormatForEdit(new byte[] { 0xCA, 0xFE }));
            Assert.Equal("text", ValueParser.FormatForEdit("text"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Parsed_values_store_with_the_expected_types()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (i INTEGER, r REAL, s TEXT, n)");
        var values = new Dictionary<string, object?>
        {
            ["@i"] = ValueParser.Parse("12", ColumnAffinity.Integer),
            ["@r"] = ValueParser.Parse("1.5", ColumnAffinity.Real),
            ["@s"] = ValueParser.Parse("0012", ColumnAffinity.Text),
            ["@n"] = ValueParser.Parse("7", ValueParser.GetAffinity("")),
        };

        await db.Session.ExecuteNonQueryAsync("INSERT INTO t VALUES (@i, @r, @s, @n)", values);
        var types = (await db.Session.QueryRowsAsync("SELECT typeof(i), typeof(r), typeof(s), typeof(n) FROM t")).Rows[0];

        Assert.Equal(["integer", "real", "text", "integer"], types);
    }
}
