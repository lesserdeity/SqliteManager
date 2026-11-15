using System.Globalization;
using System.Text;

namespace SqliteManager.Core.QueryDesign;

/// <summary>A criteria cell that cannot be turned into SQL.</summary>
public sealed class CriteriaException(string message) : Exception(message);

/// <summary>
/// Turns the text of a criteria cell into a condition on a field, the way Access does:
/// <list type="bullet">
/// <item><c>abc</c> or <c>5</c> → <c>field = 'abc'</c> / <c>field = 5</c></item>
/// <item><c>&gt;5</c>, <c>&lt;&gt;'x'</c>, <c>&gt;=date('now')</c> → comparison</item>
/// <item><c>LIKE A%</c>, <c>NOT LIKE</c>, <c>GLOB</c>, <c>IN (a, b)</c>, <c>NOT IN</c>, <c>BETWEEN 1 AND 5</c>, <c>IS NULL</c>, <c>IS NOT NULL</c></item>
/// </list>
/// Values are written as SQL literals: numbers, quoted strings ('x' or "x"), NULL/TRUE/FALSE, CURRENT_DATE and the
/// like, parameters (@p), and expressions with parentheses are kept as typed; other text is quoted as a string.
/// </summary>
public static class CriteriaParser
{
    private static readonly string[] Comparisons = [">=", "<=", "<>", "!=", "=", ">", "<"];

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "TRUE", "FALSE", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP",
    };

    /// <summary>The condition for <paramref name="fieldSql"/>, or null if <paramref name="text"/> is empty.</summary>
    public static string? ToCondition(string fieldSql, string? text)
    {
        ArgumentNullException.ThrowIfNull(fieldSql);
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return null;
        }

        if (Is(t, "IS NULL") || Is(t, "NULL"))
        {
            return $"{fieldSql} IS NULL";
        }

        if (Is(t, "IS NOT NULL") || Is(t, "NOT NULL"))
        {
            return $"{fieldSql} IS NOT NULL";
        }

        foreach (var keyword in (string[])["NOT BETWEEN", "BETWEEN"])
        {
            if (StartsWithWord(t, keyword, out var rest))
            {
                var parts = SplitOutsideQuotes(rest, " AND ");
                if (parts.Count != 2)
                {
                    throw new CriteriaException($"Write {keyword} as \"{keyword} low AND high\".");
                }

                return $"{fieldSql} {keyword} {Literal(parts[0])} AND {Literal(parts[1])}";
            }
        }

        foreach (var keyword in (string[])["NOT IN", "IN"])
        {
            if (StartsWithWord(t, keyword, out var rest) && rest.StartsWith('('))
            {
                if (!rest.EndsWith(')'))
                {
                    throw new CriteriaException($"The list after {keyword} must end with ')'.");
                }

                var inner = rest[1..^1].Trim();
                if (StartsWithWord(inner, "SELECT", out _) || StartsWithWord(inner, "WITH", out _))
                {
                    return $"{fieldSql} {keyword} ({inner})";
                }

                var items = SplitOutsideQuotes(inner, ",");
                if (items.Count == 0 || items.Any(i => i.Length == 0))
                {
                    throw new CriteriaException($"The list after {keyword} has an empty value.");
                }

                return $"{fieldSql} {keyword} ({string.Join(", ", items.Select(Literal))})";
            }
        }

        foreach (var keyword in (string[])["NOT LIKE", "LIKE", "NOT GLOB", "GLOB"])
        {
            if (StartsWithWord(t, keyword, out var rest))
            {
                return $"{fieldSql} {keyword} {Value(rest, keyword)}";
            }
        }

        foreach (var op in Comparisons)
        {
            if (t.StartsWith(op, StringComparison.Ordinal))
            {
                var sqlOp = op == "!=" ? "<>" : op;
                return $"{fieldSql} {sqlOp} {Value(t[op.Length..], op)}";
            }
        }

        return $"{fieldSql} = {Literal(t)}";
    }

    /// <summary>Writes <paramref name="value"/> as an SQL literal or expression (see the class summary).</summary>
    public static string Literal(string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
        {
            return "''";
        }

        if (IsQuoted(v, '\''))
        {
            return v;
        }

        if (IsQuoted(v, '"'))
        {
            // "text" is a string (Access style), not a column name.
            return Quote(v[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal));
        }

        if (IsNumber(v) || Keywords.Contains(v) || v[0] is '@' or ':' or '$' or '?' or '(' or '[' or '"' or '`' || v.Contains('(', StringComparison.Ordinal))
        {
            return v;
        }

        return Quote(v);
    }

    private static string Value(string rest, string after)
    {
        if (rest.Trim().Length == 0)
        {
            throw new CriteriaException($"A value is missing after {after}.");
        }

        return Literal(rest);
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool IsNumber(string v) =>
        double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _)
        && !v.EndsWith('.') && char.IsAsciiDigit(v[^1]);

    /// <summary>True if the whole text is one quoted token (doubled quotes inside are allowed).</summary>
    private static bool IsQuoted(string v, char quote)
    {
        if (v.Length < 2 || v[0] != quote || v[^1] != quote)
        {
            return false;
        }

        for (var i = 1; i < v.Length - 1; i++)
        {
            if (v[i] == quote)
            {
                if (v[i + 1] != quote || i + 1 == v.Length - 1)
                {
                    return false;
                }

                i++;
            }
        }

        return true;
    }

    private static bool Is(string text, string phrase) =>
        string.Equals(NormalizeSpaces(text), phrase, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSpaces(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True if <paramref name="text"/> starts with the words of <paramref name="phrase"/> followed by a space or '('.</summary>
    private static bool StartsWithWord(string text, string phrase, out string rest)
    {
        rest = string.Empty;
        var position = 0;
        foreach (var word in phrase.Split(' '))
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position + word.Length > text.Length
                || !text.AsSpan(position, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            position += word.Length;
        }

        if (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '(')
        {
            return false;
        }

        rest = text[position..].Trim();
        return true;
    }

    /// <summary>Splits on <paramref name="separator"/> (ignoring case) outside quotes and parentheses. Parts are trimmed.</summary>
    private static List<string> SplitOutsideQuotes(string text, string separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 0 && string.Compare(text, i, separator, 0, separator.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                i += separator.Length - 1;
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0 || parts.Count > 0)
        {
            parts.Add(current.ToString().Trim());
        }

        return parts;
    }
}
