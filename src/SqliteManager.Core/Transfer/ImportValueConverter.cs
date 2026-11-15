using System.Globalization;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;

namespace SqliteManager.Core.Transfer;

/// <summary>
/// Converts source values to values for a target column, using the column's declared type, the number
/// culture, and the date formats. Dates are stored as ISO text (2026-10-31 or 2026-10-31 14:05:00).
/// Raises <see cref="FormatException"/> for values that do not fit a numeric or date column.
/// </summary>
public sealed class ImportValueConverter
{
    private static readonly string[] IsoDateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    private readonly CultureInfo _culture;
    private readonly string[] _dateFormats;

    public ImportValueConverter(SourceOptions? options)
    {
        _culture = string.IsNullOrWhiteSpace(options?.NumberCulture) || options.NumberCulture.Equals("invariant", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(options.NumberCulture.Trim());
        _dateFormats = (options?.DateFormat ?? string.Empty)
            .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>True if the declared type is a date or time type (contains DATE or TIME).</summary>
    public static bool IsDateType(string? declaredType)
    {
        var type = (declaredType ?? string.Empty).ToUpperInvariant();
        return type.Contains("DATE", StringComparison.Ordinal) || type.Contains("TIME", StringComparison.Ordinal);
    }

    public object? Convert(object? raw, string? declaredType)
    {
        if (raw is null)
        {
            return null;
        }

        var affinity = ValueParser.GetAffinity(declaredType);
        if (IsDateType(declaredType))
        {
            return ConvertDate(raw, declaredType!);
        }

        return raw switch
        {
            string s => ConvertText(s, affinity),
            double d => affinity == ColumnAffinity.Text ? FormatNumber(d) : affinity == ColumnAffinity.Real ? d : Whole(d),
            long l => affinity == ColumnAffinity.Text ? l.ToString(CultureInfo.InvariantCulture) : affinity == ColumnAffinity.Real ? (double)l : l,
            bool b => affinity == ColumnAffinity.Text ? (b ? "true" : "false") : (b ? 1L : 0L),
            DateTime dt => FormatDate(dt, withTime: dt.TimeOfDay != TimeSpan.Zero),
            TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            _ => raw.ToString(),
        };
    }

    private object? ConvertText(string s, ColumnAffinity affinity)
    {
        if (affinity is ColumnAffinity.Text or ColumnAffinity.Blob)
        {
            return s;
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var text = s.Trim();
        if (affinity != ColumnAffinity.Real && long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, _culture, out var l))
        {
            return l;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, _culture, out var d) && double.IsFinite(d))
        {
            return affinity == ColumnAffinity.Real ? d : Whole(d);
        }

        throw new FormatException($"\"{s}\" is not a number.");
    }

    private string? ConvertDate(object raw, string declaredType)
    {
        var withTime = declaredType.Contains("TIME", StringComparison.OrdinalIgnoreCase);
        var timeOnly = withTime && !declaredType.Contains("DATE", StringComparison.OrdinalIgnoreCase);
        DateTime value;
        switch (raw)
        {
            case DateTime dt:
                value = dt;
                break;
            case double d:
                value = DateTime.FromOADate(d); // Excel serial date.
                break;
            case TimeSpan ts:
                return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            case string s when string.IsNullOrWhiteSpace(s):
                return null;
            case string s:
                if (!TryParseDate(s.Trim(), out value))
                {
                    var expected = _dateFormats.Length > 0 ? string.Join(" or ", _dateFormats) : "yyyy-MM-dd";
                    throw new FormatException($"\"{s}\" is not a date (expected {expected}).");
                }

                break;
            default:
                throw new FormatException($"\"{raw}\" is not a date.");
        }

        if (timeOnly)
        {
            return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // A DATE column still keeps a time that is not midnight, so nothing is lost.
        return FormatDate(value, withTime || value.TimeOfDay != TimeSpan.Zero);
    }

    public bool TryParseDate(string text, out DateTime value)
    {
        if (_dateFormats.Length > 0)
        {
            return DateTime.TryParseExact(text, _dateFormats, _culture, DateTimeStyles.AllowWhiteSpaces, out value);
        }

        return DateTime.TryParseExact(text, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value)
            || (!ReferenceEquals(_culture, CultureInfo.InvariantCulture)
                && DateTime.TryParseExact(text, [_culture.DateTimeFormat.ShortDatePattern, _culture.DateTimeFormat.ShortDatePattern + " " + _culture.DateTimeFormat.ShortTimePattern, _culture.DateTimeFormat.ShortDatePattern + " " + _culture.DateTimeFormat.LongTimePattern], _culture, DateTimeStyles.None, out value));
    }

    public bool TryParseNumber(string text, out double value, out bool isInteger)
    {
        isInteger = long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, _culture, out var l);
        if (isInteger)
        {
            value = l;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, _culture, out value) && double.IsFinite(value);
    }

    private static string FormatDate(DateTime value, bool withTime) =>
        value.ToString(withTime ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Cast both branches to object: "cond ? long : double" would turn the long back into a double.
    private static object Whole(double d) => d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (object)(long)d : (object)d;

    private static string FormatNumber(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15
        ? ((long)d).ToString(CultureInfo.InvariantCulture)
        : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Picks INTEGER, REAL, DATE, DATETIME, or TEXT for a new column from sample values. Text with
    /// leading zeros (such as 007) stays TEXT so the zeros are kept.
    /// </summary>
    public string InferType(IEnumerable<object?> values)
    {
        bool any = false, allInteger = true, allNumber = true, allDate = true, anyTime = false;
        foreach (var value in values)
        {
            switch (value)
            {
                case null:
                case string s when string.IsNullOrWhiteSpace(s):
                    continue;
                case long:
                case bool:
                    allDate = false;
                    break;
                case double d:
                    allDate = false;
                    allInteger &= d == Math.Floor(d) && Math.Abs(d) < 9e15;
                    break;
                case DateTime dt:
                    allInteger = allNumber = false;
                    anyTime |= dt.TimeOfDay != TimeSpan.Zero;
                    break;
                case string s:
                    var text = s.Trim();
                    var leadingZero = text.Length > 1 && text[0] == '0' && char.IsDigit(text[1]);
                    if (!leadingZero && TryParseNumber(text, out _, out var isInteger))
                    {
                        allInteger &= isInteger;
                        allDate = false;
                    }
                    else
                    {
                        allInteger = allNumber = false;
                        if (allDate && TryParseDate(text, out var date))
                        {
                            anyTime |= date.TimeOfDay != TimeSpan.Zero;
                        }
                        else
                        {
                            allDate = false;
                        }
                    }

                    break;
                default:
                    allInteger = allNumber = allDate = false;
                    break;
            }

            any = true;
            if (!allNumber && !allDate)
            {
                return "TEXT";
            }
        }

        return !any ? "TEXT"
            : allInteger ? "INTEGER"
            : allNumber ? "REAL"
            : allDate ? (anyTime ? "DATETIME" : "DATE")
            : "TEXT";
    }
}
