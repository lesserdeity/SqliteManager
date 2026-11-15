using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Tests;

public class SqlScriptSplitterTests
{
    private static string[] Texts(string script) => SqlScriptSplitter.Split(script).Select(s => s.Text).ToArray();

    [Fact]
    public void Splits_on_semicolons()
    {
        Assert.Equal(["SELECT 1", "SELECT 2", "SELECT 3"], Texts("SELECT 1; SELECT 2;\nSELECT 3"));
    }

    [Fact]
    public void Empty_and_comment_only_scripts_have_no_statements()
    {
        Assert.Empty(Texts(""));
        Assert.Empty(Texts("   \n\t"));
        Assert.Empty(Texts(";;;"));
        Assert.Empty(Texts("-- just a comment\n/* and a block */"));
    }

    [Fact]
    public void Ignores_semicolons_in_strings_names_and_comments()
    {
        var script = """
            SELECT 'a;b', "c;d", `e;f`, [g;h] FROM t; -- tail; comment
            /* block; comment */ SELECT 'it''s; fine';
            """;

        Assert.Equal(
            ["SELECT 'a;b', \"c;d\", `e;f`, [g;h] FROM t", "SELECT 'it''s; fine'"],
            Texts(script));
    }

    [Fact]
    public void Excludes_leading_comments_from_the_statement()
    {
        var statement = Assert.Single(SqlScriptSplitter.Split("-- header\n/* note */\n  SELECT 1;"));

        Assert.Equal("SELECT 1", statement.Text);
        Assert.Equal(3, statement.StartLine);
    }

    [Fact]
    public void Keeps_trigger_bodies_together()
    {
        var script = """
            CREATE TRIGGER trg AFTER INSERT ON t BEGIN
              UPDATE t SET x = 1;
              INSERT INTO log VALUES ('done; really');
            END;
            CREATE TEMP TRIGGER trg2 BEFORE DELETE ON t BEGIN SELECT 1; END;
            SELECT 2;
            """;

        var statements = SqlScriptSplitter.Split(script);

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("CREATE TRIGGER trg", statements[0].Text);
        Assert.EndsWith("END", statements[0].Text);
        Assert.StartsWith("CREATE TEMP TRIGGER trg2", statements[1].Text);
        Assert.Equal("SELECT 2", statements[2].Text);
    }

    [Fact]
    public void Statement_without_final_semicolon_is_included()
    {
        var statements = SqlScriptSplitter.Split("SELECT 1;\nSELECT 2  \n");

        Assert.Equal("SELECT 2", statements[1].Text);
        Assert.Equal(statements[1].StartOffset + "SELECT 2  \n".Length, statements[1].EndOffset);
    }

    [Fact]
    public void Records_offsets_and_lines()
    {
        const string script = "SELECT 1;\n\n  SELECT\n  2;";
        var statements = SqlScriptSplitter.Split(script);

        Assert.Equal(0, statements[0].StartOffset);
        Assert.Equal(9, statements[0].EndOffset);
        Assert.Equal(1, statements[0].StartLine);
        Assert.Equal(script.IndexOf("SELECT\n", StringComparison.Ordinal), statements[1].StartOffset);
        Assert.Equal(3, statements[1].StartLine);
        Assert.Equal(script.Length, statements[1].EndOffset);
    }

    [Theory]
    [InlineData(0, "SELECT 1")]
    [InlineData(5, "SELECT 1")]
    [InlineData(9, "SELECT 1")] // Just after the semicolon.
    [InlineData(10, "SELECT 2")] // Blank line between statements selects the next one.
    [InlineData(14, "SELECT 2")]
    [InlineData(100, "SELECT 2")] // After the end selects the last one.
    public void FindAt_returns_statement_at_caret(int caret, string expected)
    {
        const string script = "SELECT 1;\n\nSELECT 2;\n";

        Assert.Equal(expected, SqlScriptSplitter.FindAt(script, Math.Min(caret, script.Length + 50))!.Text);
    }

    [Fact]
    public void FindAt_returns_null_for_empty_script()
    {
        Assert.Null(SqlScriptSplitter.FindAt("  ", 1));
    }

    [Fact]
    public void FirstKeyword_and_ChangesSchema()
    {
        var statements = SqlScriptSplitter.Split("create table t(x); Drop view v; select 1; alter table t add y; attach 'a.db' as a; detach a");

        Assert.Equal(["CREATE", "DROP", "SELECT", "ALTER", "ATTACH", "DETACH"], statements.Select(s => s.FirstKeyword));
        Assert.Equal([true, true, false, true, true, true], statements.Select(s => s.ChangesSchema));
    }

    [Theory]
    [InlineData("SELECT 'abc' FROM t", 9, true)]
    [InlineData("SELECT 'abc' FROM t", 12, false)] // After the closing quote.
    [InlineData("SELECT 'abc", 11, true)] // Unterminated string at end.
    [InlineData("SELECT x -- note", 16, true)]
    [InlineData("SELECT x -- note\nFROM", 18, false)]
    [InlineData("SELECT /* c */ x", 10, true)]
    [InlineData("SELECT /* c */ x", 15, false)]
    [InlineData("SELECT \"col", 10, true)]
    [InlineData("SELECT a", 8, false)]
    public void IsInsideLiteralOrComment(string script, int position, bool expected)
    {
        Assert.Equal(expected, SqlScriptSplitter.IsInsideLiteralOrComment(script, position));
    }

    [Fact]
    public void LineOf_counts_newlines()
    {
        Assert.Equal(1, SqlScriptSplitter.LineOf("abc", 2));
        Assert.Equal(3, SqlScriptSplitter.LineOf("a\nb\nc", 4));
    }
}
