using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteManager.Core.Jobs;

public enum ExportFormat
{
    Csv,
    Tsv,
    Xlsx,
    Json,
    Sql,
}

public enum SourceFormat
{
    Csv,
    Tsv,
    Xlsx,
    Xls,
    Json,
}

public enum LoadMode
{
    /// <summary>Insert rows.</summary>
    Append,

    /// <summary>Delete all rows, then insert.</summary>
    Truncate,

    /// <summary>Drop the table and create it again from the source columns.</summary>
    Replace,

    /// <summary>Insert rows; update rows that match the key columns.</summary>
    Upsert,
}

public enum ErrorMode
{
    /// <summary>Stop at the first error and roll back everything.</summary>
    Rollback,

    /// <summary>Skip bad rows and write them to an error file next to the source file.</summary>
    Skip,
}

public enum IfFileExists
{
    Overwrite,
    Fail,

    /// <summary>Ask the user (in the app). The command line treats this as <see cref="Fail"/>.</summary>
    Prompt,
}

public enum CsvQuoting
{
    AsNeeded,
    Always,
    Never,
}

public enum TransactionMode
{
    /// <summary>All steps run in one transaction; a step that stops the job rolls back every step.</summary>
    Single,

    /// <summary>Each statement commits on its own.</summary>
    None,
}

public enum StepErrorMode
{
    Stop,
    Continue,
}

/// <summary>A parameter such as @Period. The value is asked for at run time (the CLI takes it as an argument).</summary>
public sealed class JobParameter
{
    public string Name { get; set; } = string.Empty;

    public string? Prompt { get; set; }

    /// <summary>Used when no value is given. An empty default means the value is required.</summary>
    public string? Default { get; set; }
}

/// <summary>A saved job. Stored in jobs.json; the "type" property selects the kind.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExportJob), "export")]
[JsonDerivedType(typeof(ImportJob), "import")]
[JsonDerivedType(typeof(ScriptJob), "script")]
public abstract class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    /// <summary>Each job stores its own database, because jobs belong to the app, not to one database.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    public List<JobParameter>? Parameters { get; set; }

    /// <summary>export, import, or script (the "type" value in jobs.json).</summary>
    [JsonIgnore]
    public string TypeName => this switch
    {
        ExportJob => "export",
        ImportJob => "import",
        _ => "script",
    };

    /// <summary>A deep copy with a new id.</summary>
    public Job Duplicate(string newName)
    {
        var copy = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(this, JobJson.Options), JobJson.Options)!;
        copy.Id = Guid.NewGuid().ToString();
        copy.Name = newName;
        return copy;
    }
}

public sealed class ExportJob : Job
{
    /// <summary>The query to export. Either this or <see cref="Table"/>.</summary>
    public string? Sql { get; set; }

    /// <summary>A table or view to export instead of <see cref="Sql"/>.</summary>
    public string? Table { get; set; }

    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    /// <summary>File path. May contain {date}, {datetime}, and {jobname}.</summary>
    public string OutputPath { get; set; } = string.Empty;

    public ExportOptions? Options { get; set; }

    public IfFileExists IfFileExists { get; set; } = IfFileExists.Prompt;
}

public sealed class ExportOptions
{
    public string? SheetName { get; set; }

    public bool? IncludeHeader { get; set; }

    public bool? AutoFit { get; set; }

    public bool? FreezeHeader { get; set; }

    public string? Delimiter { get; set; }

    public CsvQuoting? Quoting { get; set; }

    /// <summary>utf-8 (with BOM), utf-8-nobom, utf-16, or windows-1252.</summary>
    public string? Encoding { get; set; }

    /// <summary>Table name used in SQL INSERT scripts.</summary>
    public string? TableName { get; set; }
}

public sealed class ImportJob : Job
{
    public string SourcePath { get; set; } = string.Empty;

    public SourceFormat SourceFormat { get; set; } = SourceFormat.Csv;

    public SourceOptions? SourceOptions { get; set; }

    public string TargetTable { get; set; } = string.Empty;

    public LoadMode LoadMode { get; set; } = LoadMode.Append;

    public List<string>? KeyColumns { get; set; }

    public bool CreateTableIfMissing { get; set; }

    /// <summary>Source column to target column. Empty means: match columns by name.</summary>
    public List<ColumnMapping>? ColumnMappings { get; set; }

    public ErrorMode OnError { get; set; } = ErrorMode.Rollback;

    /// <summary>SQL run before the rows, in the same transaction.</summary>
    public string? PreSql { get; set; }

    /// <summary>SQL run after the rows, in the same transaction.</summary>
    public string? PostSql { get; set; }
}

public sealed class SourceOptions
{
    /// <summary>Field delimiter for CSV/TSV. "\t" or "tab" means tab.</summary>
    public string? Delimiter { get; set; }

    public string? Quote { get; set; }

    /// <summary>File encoding, for example utf-8, utf-16, windows-1252.</summary>
    public string? Encoding { get; set; }

    /// <summary>True if the first row (after skipped rows / at the start cell) holds column names.</summary>
    public bool? HasHeader { get; set; }

    /// <summary>CSV lines to skip before the header or data.</summary>
    public int? SkipRows { get; set; }

    public string? SheetName { get; set; }

    /// <summary>Top-left cell of the data in Excel, for example A1 or B3.</summary>
    public string? StartCell { get; set; }

    /// <summary>Date formats to read, separated by ';', for example dd/MM/yyyy;dd/MM/yyyy HH:mm.</summary>
    public string? DateFormat { get; set; }

    /// <summary>Culture for numbers and dates, for example en-US or de-DE. Empty means invariant (1234.5).</summary>
    public string? NumberCulture { get; set; }
}

/// <summary>Maps a source column to a target column, or gives a target column a default value (no source).</summary>
public sealed class ColumnMapping
{
    public string? Source { get; set; }

    public string Target { get; set; } = string.Empty;

    public string? Default { get; set; }
}

public sealed class ScriptJob : Job
{
    public TransactionMode TransactionMode { get; set; } = TransactionMode.Single;

    public List<ScriptStep> Steps { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SqlStep), "sql")]
[JsonDerivedType(typeof(RunJobStep), "runJob")]
public abstract class ScriptStep
{
    public string Name { get; set; } = string.Empty;
}

public sealed class SqlStep : ScriptStep
{
    public string? Sql { get; set; }

    /// <summary>A .sql file to run instead of <see cref="Sql"/>.</summary>
    public string? SqlFile { get; set; }

    public StepErrorMode OnError { get; set; } = StepErrorMode.Stop;

    public int? TimeoutSeconds { get; set; }
}

public sealed class RunJobStep : ScriptStep
{
    public string Job { get; set; } = string.Empty;

    /// <summary>Stop (the default) or continue when the job fails.</summary>
    public StepErrorMode? OnError { get; set; }
}

/// <summary>The contents of jobs.json.</summary>
public sealed class JobFile
{
    public int Version { get; set; } = 1;

    public List<Job> Jobs { get; set; } = [];

    public Job? Find(string name) => Jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>JSON settings for jobs.json: camelCase names, enum values like "xlsx" and "runJob", nulls left out.</summary>
public static class JobJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Keep < > & ' readable in SQL.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
