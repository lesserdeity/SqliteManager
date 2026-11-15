using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using SqliteManager.Core.Jobs;

namespace SqliteManager.Core.Transfer;

/// <summary>
/// One row read from a source file. <see cref="Number"/> is the line or sheet row (1-based) for error messages.
/// Values are strings (CSV), typed cell values (Excel: double, DateTime, bool, string), or JSON values.
/// <see cref="Error"/> is set when the row itself is malformed.
/// </summary>
public sealed record SourceRow(long Number, object?[] Values, string? Error = null);

/// <summary>Reads column names and then rows from an import source.</summary>
public interface ISourceReader : IDisposable
{
    IReadOnlyList<string> Columns { get; }

    IEnumerable<SourceRow> ReadRows();
}

/// <summary>Opens the reader for a file format.</summary>
public static class SourceReaders
{
    public static ISourceReader Open(string path, SourceFormat format, SourceOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The file \"{path}\" does not exist.", path);
        }

        options ??= new SourceOptions();
        return format switch
        {
            SourceFormat.Csv => new CsvSourceReader(path, options, ","),
            SourceFormat.Tsv => new CsvSourceReader(path, options, "\t"),
            SourceFormat.Xlsx => new XlsxSourceReader(path, options),
            SourceFormat.Xls => new XlsSourceReader(path, options),
            _ => new JsonSourceReader(path),
        };
    }

    /// <summary>The format suggested by a file's extension.</summary>
    public static SourceFormat FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tsv" or ".tab" => SourceFormat.Tsv,
        ".xlsx" or ".xlsm" => SourceFormat.Xlsx,
        ".xls" => SourceFormat.Xls,
        ".json" => SourceFormat.Json,
        _ => SourceFormat.Csv,
    };

    /// <summary>Sheet names of an Excel file.</summary>
    public static IReadOnlyList<string> ListSheets(string path, SourceFormat format)
    {
        if (format == SourceFormat.Xlsx)
        {
            using var stream = OpenShared(path);
            using var workbook = new XLWorkbook(stream);
            return workbook.Worksheets.Select(w => w.Name).ToList();
        }

        TextEncodings.EnsureRegistered();
        using var xls = OpenShared(path);
        using var reader = ExcelReaderFactory.CreateReader(xls);
        var names = new List<string>();
        do
        {
            names.Add(reader.Name);
        }
        while (reader.NextResult());

        return names;
    }

    /// <summary>Reads up to <paramref name="maxRows"/> rows for the import preview.</summary>
    public static (IReadOnlyList<string> Columns, IReadOnlyList<SourceRow> Rows) Preview(string path, SourceFormat format, SourceOptions? options, int maxRows = 100)
    {
        using var reader = Open(path, format, options);
        return (reader.Columns, reader.ReadRows().Take(maxRows).ToList());
    }

    /// <summary>Opens a file for reading even if another program (such as Excel) has it open.</summary>
    internal static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Names for columns: blank names become ColumnN, repeated names get a number.</summary>
    internal static List<string> CleanNames(IEnumerable<string?> names)
    {
        var result = new List<string>();
        var index = 0;
        foreach (var raw in names)
        {
            index++;
            var name = string.IsNullOrWhiteSpace(raw) ? $"Column{index}" : raw.Trim();
            var unique = name;
            for (var n = 2; result.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
            {
                unique = $"{name}_{n}";
            }

            result.Add(unique);
        }

        return result;
    }

    /// <summary>Parses a cell reference such as B3 into a 1-based row and column.</summary>
    public static (int Row, int Column) ParseCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return (1, 1);
        }

        var text = cell.Trim().ToUpperInvariant();
        var i = 0;
        var column = 0;
        while (i < text.Length && text[i] is >= 'A' and <= 'Z')
        {
            column = (column * 26) + (text[i] - 'A' + 1);
            i++;
        }

        if (column == 0 || i == text.Length || !int.TryParse(text[i..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1)
        {
            throw new FormatException($"\"{cell}\" is not a cell reference such as A1 or B3.");
        }

        return (row, column);
    }
}

internal sealed class CsvSourceReader : ISourceReader
{
    private readonly StreamReader _text;
    private readonly CsvParser _parser;
    private readonly string[]? _firstData;
    private readonly long _firstDataRow;

    public CsvSourceReader(string path, SourceOptions options, string defaultDelimiter)
    {
        var delimiter = options.Delimiter switch
        {
            null or "" => defaultDelimiter,
            "\\t" or "tab" or "Tab" or "TAB" => "\t",
            var d => d,
        };
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            Quote = string.IsNullOrEmpty(options.Quote) ? '"' : options.Quote[0],
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            DetectDelimiter = false,
        };

        _text = new StreamReader(SourceReaders.OpenShared(path), TextEncodings.Get(options.Encoding, new UTF8Encoding(false)), detectEncodingFromByteOrderMarks: true);
        _parser = new CsvParser(_text, config);

        for (var i = 0; i < (options.SkipRows ?? 0) && _parser.Read(); i++)
        {
        }

        if (options.HasHeader ?? true)
        {
            Columns = _parser.Read() ? SourceReaders.CleanNames(_parser.Record!) : [];
        }
        else
        {
            // Without a header the first data row decides how many columns there are.
            if (_parser.Read())
            {
                _firstData = _parser.Record;
                _firstDataRow = _parser.RawRow;
            }

            Columns = SourceReaders.CleanNames(Enumerable.Repeat<string?>(null, _firstData?.Length ?? 0));
        }
    }

    public IReadOnlyList<string> Columns { get; }

    public IEnumerable<SourceRow> ReadRows()
    {
        if (_firstData is not null)
        {
            yield return ToRow(_firstDataRow, _firstData);
        }

        while (_parser.Read())
        {
            yield return ToRow(_parser.RawRow, _parser.Record!);
        }
    }

    private SourceRow ToRow(long number, string[] fields)
    {
        var values = new object?[Columns.Count];
        for (var i = 0; i < values.Length && i < fields.Length; i++)
        {
            values[i] = fields[i];
        }

        var error = fields.Length > Columns.Count && fields.Skip(Columns.Count).Any(f => f.Length > 0)
            ? $"The row has {fields.Length} fields; the header has {Columns.Count}."
            : null;
        return new SourceRow(number, values, error);
    }

    public void Dispose()
    {
        _parser.Dispose();
        _text.Dispose();
    }
}

internal sealed class XlsxSourceReader : ISourceReader
{
    private readonly FileStream _stream;
    private readonly XLWorkbook _workbook;
    private readonly IXLWorksheet _sheet;
    private readonly int _firstDataRow;
    private readonly int _firstColumn;
    private readonly int _lastRow;

    public XlsxSourceReader(string path, SourceOptions options)
    {
        _stream = SourceReaders.OpenShared(path);
        _workbook = new XLWorkbook(_stream);
        _sheet = string.IsNullOrWhiteSpace(options.SheetName)
            ? _workbook.Worksheets.First()
            : _workbook.Worksheets.FirstOrDefault(w => w.Name.Equals(options.SheetName, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"The workbook has no sheet named \"{options.SheetName}\".");

        var (startRow, startColumn) = SourceReaders.ParseCell(options.StartCell);
        _firstColumn = startColumn;
        _lastRow = _sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = Math.Max(_sheet.LastColumnUsed()?.ColumnNumber() ?? 0, startColumn - 1);
        var width = lastColumn - startColumn + 1;

        if (options.HasHeader ?? true)
        {
            Columns = SourceReaders.CleanNames(Enumerable.Range(startColumn, width).Select(c => _sheet.Cell(startRow, c).GetFormattedString()));
            _firstDataRow = startRow + 1;
        }
        else
        {
            Columns = SourceReaders.CleanNames(Enumerable.Repeat<string?>(null, width));
            _firstDataRow = startRow;
        }
    }

    public IReadOnlyList<string> Columns { get; }

    public IEnumerable<SourceRow> ReadRows()
    {
        for (var r = _firstDataRow; r <= _lastRow; r++)
        {
            var values = new object?[Columns.Count];
            var any = false;
            for (var c = 0; c < values.Length; c++)
            {
                values[c] = CellValue(_sheet.Cell(r, _firstColumn + c).Value);
                any |= values[c] is not null;
            }

            if (any)
            {
                yield return new SourceRow(r, values);
            }
        }
    }

    private static object? CellValue(XLCellValue value) => value.Type switch
    {
        XLDataType.Blank => null,
        XLDataType.Boolean => value.GetBoolean(),
        XLDataType.Number => value.GetNumber(),
        XLDataType.DateTime => value.GetDateTime(),
        XLDataType.TimeSpan => value.GetTimeSpan(),
        XLDataType.Error => "#" + value.GetError(),
        _ => value.GetText(),
    };

    public void Dispose()
    {
        _workbook.Dispose();
        _stream.Dispose();
    }
}

internal sealed class XlsSourceReader : ISourceReader
{
    private readonly FileStream _stream;
    private readonly IExcelDataReader _reader;
    private readonly int _firstColumn;
    private readonly List<(long Number, object?[] Values)> _buffered = [];
    private readonly bool _hasHeader;
    private readonly int _startRow;
    private int _currentRow;

    public XlsSourceReader(string path, SourceOptions options)
    {
        TextEncodings.EnsureRegistered();
        _stream = SourceReaders.OpenShared(path);
        _reader = ExcelReaderFactory.CreateReader(_stream);
        if (!string.IsNullOrWhiteSpace(options.SheetName))
        {
            while (!_reader.Name.Equals(options.SheetName, StringComparison.OrdinalIgnoreCase))
            {
                if (!_reader.NextResult())
                {
                    throw new ArgumentException($"The workbook has no sheet named \"{options.SheetName}\".");
                }
            }
        }

        (_startRow, _firstColumn) = SourceReaders.ParseCell(options.StartCell);
        _hasHeader = options.HasHeader ?? true;

        // Skip rows above the start cell, then read the header (or the first row) to learn the width.
        while (_currentRow < _startRow - 1 && _reader.Read())
        {
            _currentRow++;
        }

        object?[] first = [];
        if (_reader.Read())
        {
            _currentRow++;
            first = ReadCurrent();
        }

        if (_hasHeader)
        {
            Columns = SourceReaders.CleanNames(first.Select(v => v?.ToString()));
        }
        else
        {
            Columns = SourceReaders.CleanNames(Enumerable.Repeat<string?>(null, first.Length));
            if (first.Any(v => v is not null))
            {
                _buffered.Add((_currentRow, first));
            }
        }
    }

    public IReadOnlyList<string> Columns { get; }

    public IEnumerable<SourceRow> ReadRows()
    {
        foreach (var (number, values) in _buffered)
        {
            yield return new SourceRow(number, Fit(values));
        }

        while (_reader.Read())
        {
            _currentRow++;
            var values = Fit(ReadCurrent());
            if (values.Any(v => v is not null))
            {
                yield return new SourceRow(_currentRow, values);
            }
        }
    }

    private object?[] ReadCurrent()
    {
        var count = Math.Max(0, _reader.FieldCount - (_firstColumn - 1));
        var values = new object?[count];
        for (var i = 0; i < count; i++)
        {
            var value = _reader.GetValue(_firstColumn - 1 + i);
            values[i] = value is DBNull ? null : value;
        }

        // Trim empty cells at the end so the header width is the used width.
        var last = Array.FindLastIndex(values, v => v is not null && !(v is string s && s.Length == 0));
        return values[..(last + 1)];
    }

    private object?[] Fit(object?[] values)
    {
        var fitted = new object?[Columns.Count];
        Array.Copy(values, fitted, Math.Min(values.Length, fitted.Length));
        return fitted;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

internal sealed class JsonSourceReader : ISourceReader
{
    private readonly JsonDocument _document;

    public JsonSourceReader(string path)
    {
        using (var stream = SourceReaders.OpenShared(path))
        {
            _document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }

        if (_document.RootElement.ValueKind != JsonValueKind.Array)
        {
            _document.Dispose();
            throw new FormatException("The JSON file must contain an array of objects, for example [{\"id\": 1}, {\"id\": 2}].");
        }

        // Every key that appears, in order of first appearance.
        var names = new List<string>();
        foreach (var item in _document.RootElement.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            foreach (var property in item.EnumerateObject())
            {
                if (!names.Contains(property.Name, StringComparer.Ordinal))
                {
                    names.Add(property.Name);
                }
            }
        }

        Columns = names;
    }

    public IReadOnlyList<string> Columns { get; }

    public IEnumerable<SourceRow> ReadRows()
    {
        var number = 0L;
        foreach (var item in _document.RootElement.EnumerateArray())
        {
            number++;
            var values = new object?[Columns.Count];
            if (item.ValueKind != JsonValueKind.Object)
            {
                yield return new SourceRow(number, values, $"Item {number} is not an object.");
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                var index = Columns.ToList().IndexOf(property.Name);
                if (index >= 0)
                {
                    values[index] = ToValue(property.Value);
                }
            }

            yield return new SourceRow(number, values);
        }
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : (object)value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.GetRawText(),
    };

    public void Dispose() => _document.Dispose();
}
