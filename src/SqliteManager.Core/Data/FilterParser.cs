using System.Globalization;
using System.Text;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Data;

/// <summary>A WHERE condition (without the WHERE keyword) and its parameters.</summary>
public sealed record FilterCondition(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    public static FilterCondition Empty { get; } = new(string.Empty, new Dictionary<string, object?>());

    public bool IsEmpty => Sql.Length == 0;
}

/// <summary>
/// Turns filter-row text into SQL. Syntax per column:
/// plain text = contains (LIKE '%text%');
/// <c>=x</c>, <c>&lt;&gt;x</c> (or <c>!=x</c>), <c>&gt;x</c>, <c>&gt;=x</c>, <c>&lt;x</c>, <c>&lt;=x</c> = comparison;
/// <c>IS NULL</c>, <c>IS NOT NULL</c>. Values that look like numbers are compared as numbers.
/// </summary>
public static class FilterParser
{
    private static readonly string[] Operators = [">=", "<=", "<>", "!=", "=", ">", "<"];

    /// <summary>Builds one condition from all filters, joined with AND. Filters on unknown columns are ignored.</summary>
    public static FilterCondition Build(IEnumerable<ColumnFilter> filters, IReadOnlyCollection<string> columns)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(columns);

        var parameters = new Dictionary<string, object?>();
        var parts = new List<string>();
        foreach (var filter in filters)
        {
            var column = columns.FirstOrDefault(c => string.Equals(c, filter.Column, StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                continue;
            }

            var part = BuildOne(column, filter.Text, parameters);
            if (part is not null)
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? FilterCondition.Empty : new FilterCondition(string.Join(" AND ", parts), parameters);
    }

    private static string? BuildOne(string column, string? text, Dictionary<string, object?> parameters)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        var name = SqlIdentifier.Quote(column);
        if (trimmed.Equals("IS NULL", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} IS NULL";
        }

        if (trimmed.Equals("IS NOT NULL", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} IS NOT NULL";
        }

        var parameter = $"@f{parameters.Count}";
        foreach (var op in Operators)
        {
            if (trimmed.StartsWith(op, StringComparison.Ordinal))
            {
                var value = trimmed[op.Length..].Trim();
                parameters[parameter] = ParseLiteral(value);
                var sqlOp = op == "!=" ? "<>" : op;
                return $"{name} {sqlOp} {parameter}";
            }
        }

        parameters[parameter] = "%" + EscapeLike(trimmed) + "%";
        return $"{name} LIKE {parameter} ESCAPE '\\'";
    }

    /// <summary>A number if the text is a number (invariant culture), otherwise the text.</summary>
    public static object ParseLiteral(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return text;
    }

    private static string EscapeLike(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
