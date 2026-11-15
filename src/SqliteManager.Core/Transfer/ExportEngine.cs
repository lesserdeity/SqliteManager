using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Transfer;

public sealed record ExportResult(string FilePath, long Rows);

/// <summary>The export stopped. No file was written or replaced.</summary>
public sealed class ExportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Runs a query (or reads a table) and writes the rows to a file. The file is replaced only when the export succeeds.</summary>
public static class ExportEngine
{
    /// <summary>Excel's limit, including the header row.</summary>
    public const int MaxExcelRows = 1_048_576;

    public static Task<ExportResult> RunAsync(
        DatabaseSession session,
        ExportJob job,
        IReadOnlyDictionary<string, object?> parameters,
        Func<string, bool?>? confirmOverwrite = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RunExclusiveAsync(runner => Run(runner, job, parameters, confirmOverwrite, progress, cancellationToken), cancellationToken);
    }

    /// <param name="confirmOverwrite">
    /// Asked when the file exists and the job says Prompt. Returning null (or passing no function) means
    /// "cannot ask", which is treated as fail.
    /// </param>
    public static ExportResult Run(
        ISqlRunner runner,
        ExportJob job,
        IReadOnlyDictionary<string, object?> parameters,
        Func<string, bool?>? confirmOverwrite,
        IProgress<long>? progress,
        CancellationToken cancellationToken,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(job);
        var path = Path.GetFullPath(ResolvePath(job.OutputPath, job.Name, now ?? DateTime.Now));
        if (File.Exists(path))
        {
            var answer = job.IfFileExists switch
            {
                IfFileExists.Overwrite => true,
                IfFileExists.Prompt => confirmOverwrite?.Invoke(path),
                _ => false,
            };
            if (answer != true)
            {
                throw new ExportException(answer is null
                    ? $"The file \"{path}\" already exists. The job is set to ask before replacing it, which the command line treats as \"fail\"."
                    : $"The file \"{path}\" already exists, so nothing was exported.");
            }
        }

        var sql = !string.IsNullOrWhiteSpace(job.Sql) ? job.Sql.Trim().TrimEnd(';')
            : !string.IsNullOrWhiteSpace(job.Table) ? $"SELECT * FROM {SqlIdentifier.Quote(job.Table)}"
            : throw new ExportException("Choose a table or enter SQL to export.");

        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        long rows = 0;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var writer = CreateWriter(job, stream))
            {
                runner.QueryEach(sql, parameters, writer.Begin, values =>
                {
                    writer.Row(values);
                    if (++rows % 1000 == 0)
                    {
                        progress?.Report(rows);
                    }
                });
                writer.Complete();
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        progress?.Report(rows);
        return new ExportResult(path, rows);
    }

    /// <summary>Replaces {date} (yyyy-MM-dd), {datetime} (yyyy-MM-dd_HHmmss), and {jobname} in a file name.</summary>
    public static string ResolvePath(string template, string jobName, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template, nameof(template));
        var safeName = string.Concat((jobName ?? string.Empty).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return template
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{datetime}", now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{jobname}", safeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The usual file extension for a format.</summary>
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Tsv => ".tsv",
        ExportFormat.Xlsx => ".xlsx",
        ExportFormat.Json => ".json",
        ExportFormat.Sql => ".sql",
        _ => ".csv",
    };

    private static IExportWriter CreateWriter(ExportJob job, Stream stream)
    {
        var options = job.Options ?? new ExportOptions();
        return job.Format switch
        {
            ExportFormat.Csv or ExportFormat.Tsv => new CsvExportWriter(stream, options, job.Format == ExportFormat.Tsv ? "\t" : ","),
            ExportFormat.Xlsx => new XlsxExportWriter(stream, options, job.Name),
            ExportFormat.Json => new JsonExportWriter(stream),
            _ => new SqlExportWriter(stream, options, job.Table),
        };
    }
}

/// <summary>Writes rows in one file format.</summary>
public interface IExportWriter : IDisposable
{
    void Begin(IReadOnlyList<ResultColumn> columns);

    void Row(object?[] values);

    void Complete();
}

internal sealed class CsvExportWriter : IExportWriter
{
    private readonly StreamWriter _text;
    private readonly CsvWriter _csv;
    private readonly bool _header;

    public CsvExportWriter(Stream stream, ExportOptions options, string defaultDelimiter)
    {
        var delimiter = options.Delimiter is "\\t" or "tab" or "Tab" ? "\t" : string.IsNullOrEmpty(options.Delimiter) ? defaultDelimiter : options.Delimiter;
        var quoting = options.Quoting ?? CsvQuoting.AsNeeded;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            NewLine = "\r\n",
            ShouldQuote = quoting switch
            {
                CsvQuoting.Always => _ => true,
                CsvQuoting.Never => _ => false,
                _ => args => args.Field is { } f && (f.Contains(delimiter, StringComparison.Ordinal) || f.IndexOfAny(['"', '\r', '\n']) >= 0 || (f.Length > 0 && (f[0] == ' ' || f[^1] == ' '))),
            },
            Mode = quoting == CsvQuoting.Never ? CsvMode.NoEscape : CsvMode.RFC4180,
        };

        // CSV defaults to UTF-8 with a byte order mark so Excel shows accented characters correctly.
        _text = new StreamWriter(stream, TextEncodings.Get(options.Encoding, new UTF8Encoding(true)), leaveOpen: true);
        _csv = new CsvWriter(_text, config);
        _header = options.IncludeHeader ?? true;
    }

    public void Begin(IReadOnlyList<ResultColumn> columns)
    {
        if (!_header)
        {
            return;
        }

        foreach (var column in columns)
        {
            _csv.WriteField(column.Name);
        }

        _csv.NextRecord();
    }

    public void Row(object?[] values)
    {
        foreach (var value in values)
        {
            _csv.WriteField(ValueParser.FormatForEdit(value));
        }

        _csv.NextRecord();
    }

    public void Complete() => _csv.Flush();

    public void Dispose()
    {
        _csv.Dispose();
        _text.Dispose();
    }
}

internal sealed class XlsxExportWriter(Stream stream, ExportOptions options, string jobName) : IExportWriter
{
    private readonly XLWorkbook _workbook = new();
    private IXLWorksheet? _sheet;
    private int _row;
    private int _columns;

    public void Begin(IReadOnlyList<ResultColumn> columns)
    {
        _sheet = _workbook.Worksheets.Add(SheetName(options.SheetName ?? (string.IsNullOrWhiteSpace(jobName) ? "Sheet1" : jobName)));
        _columns = columns.Count;
        if (options.IncludeHeader ?? true)
        {
            _row = 1;
            for (var c = 0; c < columns.Count; c++)
            {
                _sheet.Cell(1, c + 1).Value = columns[c].Name;
            }

            _sheet.Row(1).Style.Font.Bold = true;
        }
    }

    public void Row(object?[] values)
    {
        if (++_row > ExportEngine.MaxExcelRows)
        {
            throw new ExportException($"Excel sheets hold at most {ExportEngine.MaxExcelRows:N0} rows. Export to CSV, or filter the query.");
        }

        for (var c = 0; c < values.Length; c++)
        {
            var cell = _sheet!.Cell(_row, c + 1);
            switch (values[c])
            {
                case null:
                    break;
                case long l:
                    cell.Value = l;
                    break;
                case double d:
                    cell.Value = d;
                    break;
                case byte[] b:
                    cell.Value = Convert.ToHexString(b);
                    break;
                case var v:
                    cell.Value = v.ToString();
                    break;
            }
        }
    }

    public void Complete()
    {
        _sheet ??= _workbook.Worksheets.Add("Sheet1");
        if ((options.FreezeHeader ?? false) && (options.IncludeHeader ?? true))
        {
            _sheet.SheetView.FreezeRows(1);
        }

        if (options.AutoFit ?? false)
        {
            _sheet.Columns(1, Math.Max(1, _columns)).AdjustToContents();
        }

        _workbook.SaveAs(stream);
    }

    public void Dispose() => _workbook.Dispose();

    /// <summary>Sheet names: at most 31 characters, none of []:*?/\.</summary>
    private static string SheetName(string name)
    {
        var clean = string.Concat(name.Select(c => "[]:*?/\\".Contains(c) ? '_' : c)).Trim('\'');
        if (clean.Length == 0)
        {
            clean = "Sheet1";
        }

        return clean.Length > 31 ? clean[..31] : clean;
    }
}

internal sealed class JsonExportWriter(Stream stream) : IExportWriter
{
    private readonly Utf8JsonWriter _json = new(stream, new JsonWriterOptions { Indented = true });
    private string[] _names = [];

    public void Begin(IReadOnlyList<ResultColumn> columns)
    {
        _names = columns.Select(c => c.Name).ToArray();
        _json.WriteStartArray();
    }

    public void Row(object?[] values)
    {
        _json.WriteStartObject();
        for (var i = 0; i < values.Length; i++)
        {
            _json.WritePropertyName(_names[i]);
            switch (values[i])
            {
                case null:
                    _json.WriteNullValue();
                    break;
                case long l:
                    _json.WriteNumberValue(l);
                    break;
                case double d when double.IsFinite(d):
                    _json.WriteNumberValue(d);
                    break;
                case double d:
                    _json.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                    break;
                case byte[] b:
                    _json.WriteBase64StringValue(b);
                    break;
                case var v:
                    _json.WriteStringValue(v.ToString());
                    break;
            }
        }

        _json.WriteEndObject();
    }

    public void Complete()
    {
        if (_json.CurrentDepth == 0 && _json.BytesCommitted == 0 && _json.BytesPending == 0)
        {
            _json.WriteStartArray();
        }

        _json.WriteEndArray();
        _json.Flush();
    }

    public void Dispose() => _json.Dispose();
}

internal sealed class SqlExportWriter(Stream stream, ExportOptions options, string? table) : IExportWriter
{
    private readonly StreamWriter _text = new(stream, TextEncodings.Get(options.Encoding, new UTF8Encoding(false)), leaveOpen: true);
    private string _prefix = string.Empty;

    public void Begin(IReadOnlyList<ResultColumn> columns)
    {
        var name = options.TableName ?? table ?? "exported";
        _prefix = $"INSERT INTO {SqlIdentifier.Quote(name)} ({string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name)))}) VALUES (";
    }

    public void Row(object?[] values)
    {
        _text.Write(_prefix);
        _text.Write(string.Join(", ", values.Select(Literal)));
        _text.Write(");\r\n");
    }

    public void Complete() => _text.Flush();

    public void Dispose() => _text.Dispose();

    /// <summary>A SQL literal: NULL, a number, 'text' (quotes doubled), or X'hex'.</summary>
    public static string Literal(object? value) => value switch
    {
        null => "NULL",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
        double d => d > 0 ? "9e999" : d < 0 ? "-9e999" : "NULL",
        byte[] b => $"X'{Convert.ToHexString(b)}'",
        var v => "'" + v.ToString()!.Replace("'", "''", StringComparison.Ordinal) + "'",
    };
}
