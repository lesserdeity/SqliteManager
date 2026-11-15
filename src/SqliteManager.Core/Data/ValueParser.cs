using System.Globalization;

namespace SqliteManager.Core.Data;

/// <summary>SQLite column affinity (https://sqlite.org/datatype3.html#type_affinity).</summary>
public enum ColumnAffinity
{
    Integer,
    Text,
    Blob,
    Real,
    Numeric,
}

/// <summary>Converts between cell text and SQLite values. Numbers use the invariant culture (for example 1234.5).</summary>
public static class ValueParser
{
    private const NumberStyles IntegerStyle = NumberStyles.AllowLeadingSign;
    private const NumberStyles RealStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    /// <summary>Affinity from a declared column type, using SQLite's rules in order.</summary>
    public static ColumnAffinity GetAffinity(string? declaredType)
    {
        var type = (declaredType ?? string.Empty).ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return ColumnAffinity.Integer;
        }

        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal))
        {
            return ColumnAffinity.Text;
        }

        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal) || type == "ANY")
        {
            return ColumnAffinity.Blob;
        }

        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal))
        {
            return ColumnAffinity.Real;
        }

        return ColumnAffinity.Numeric;
    }

    /// <summary>
    /// Turns typed text into a value for a column. TEXT columns keep the text. Other columns store
    /// well-formed numbers as numbers (no spaces or thousands separators); anything else stays text.
    /// Empty text is an empty string, never NULL.
    /// </summary>
    public static object Parse(string text, ColumnAffinity affinity)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (affinity == ColumnAffinity.Text || text.Length == 0)
        {
            return text;
        }

        if (affinity != ColumnAffinity.Real && long.TryParse(text, IntegerStyle, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (double.TryParse(text, RealStyle, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            // INTEGER and NUMERIC columns store whole-number reals as integers, like SQLite does.
            if (affinity is ColumnAffinity.Integer or ColumnAffinity.Numeric && d == Math.Floor(d) && Math.Abs(d) < 9.2e18)
            {
                return (long)d;
            }

            return d;
        }

        return text;
    }

    /// <summary>Text shown in the grid and in the cell editor. NULL is empty here; the grid draws NULL itself.</summary>
    public static string FormatForEdit(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        _ => value.ToString() ?? string.Empty,
    };
}
