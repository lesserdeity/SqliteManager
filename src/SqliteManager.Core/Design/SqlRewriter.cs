using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Design;

/// <summary>Small token-based edits to SQL text.</summary>
public static class SqlRewriter
{
    /// <summary>
    /// Replaces every name token equal to <paramref name="oldName"/> (bare or quoted, ignoring case) with the
    /// quoted <paramref name="newName"/>. Strings and comments are left alone. Returns the input if it cannot be read.
    /// </summary>
    public static string RenameIdentifier(string sql, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(sql);
        List<SqlToken> tokens;
        try
        {
            tokens = SqlTokenizer.Tokenize(sql);
        }
        catch (SqlParseException)
        {
            return sql;
        }

        var result = sql;
        foreach (var token in tokens.Where(t => t.IsName && t.Value.Equals(oldName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Start))
        {
            result = result[..token.Start] + SqlIdentifier.Quote(newName) + result[token.End..];
        }

        return result;
    }

    /// <summary>Removes the indentation shared by all lines after the first (the first line is already trimmed).</summary>
    public static string Dedent(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 2)
        {
            return text;
        }

        var indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select((l, i) => i == 0 || l.Length < indent ? l.TrimEnd() : l[indent..].TrimEnd()));
    }
}
