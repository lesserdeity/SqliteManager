using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Tests;

public class SqlIdentifierTests
{
    [Theory]
    [InlineData("users", "\"users\"")]
    [InlineData("order details", "\"order details\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("\"", "\"\"\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("Größe_表", "\"Größe_表\"")]
    [InlineData("select", "\"select\"")]
    public void Quote_wraps_and_escapes(string input, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Quote(input));
    }

    [Fact]
    public void Quote_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => SqlIdentifier.Quote(null!));
    }

    [Fact]
    public async Task Quoted_names_work_in_sqlite()
    {
        const string name = "weird \"table\" name";
        await using var db = await TestDatabase.CreateAsync(
            $"CREATE TABLE {SqlIdentifier.Quote(name)} ({SqlIdentifier.Quote("a \"b\"")} INTEGER)",
            $"INSERT INTO {SqlIdentifier.Quote(name)} VALUES (1)");

        var count = await db.Session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {SqlIdentifier.Quote(name)}");

        Assert.Equal(1, count);
    }
}
