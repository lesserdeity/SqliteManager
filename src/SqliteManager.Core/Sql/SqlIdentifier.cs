namespace SqliteManager.Core.Sql;

/// <summary>Helpers for writing SQLite identifiers safely.</summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Wraps an identifier in double quotes and doubles any embedded double quotes.
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
