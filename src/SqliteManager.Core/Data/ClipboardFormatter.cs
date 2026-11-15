using System.Text;

namespace SqliteManager.Core.Data;

/// <summary>Formats grid cells as tab-separated text with a header row, for the clipboard.</summary>
public static class ClipboardFormatter
{
    /// <summary>
    /// Builds tab-separated text. NULL becomes an empty field and BLOBs become hex. Fields that contain
    /// tabs, line breaks, or double quotes are wrapped in double quotes, with inner quotes doubled.
    /// </summary>
    public static string ToTsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        AppendLine(builder, headers);
        foreach (var row in rows)
        {
            AppendLine(builder, row.Select(ValueParser.FormatForEdit).ToArray());
        }

        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\t');
            }

            builder.Append(Quote(fields[i]));
        }

        builder.Append("\r\n");
    }

    private static string Quote(string field) =>
        field.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0 || field.Contains('"', StringComparison.Ordinal)
            ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : field;
}
