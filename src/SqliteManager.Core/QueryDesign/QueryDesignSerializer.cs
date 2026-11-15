using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqliteManager.Core.IO;

namespace SqliteManager.Core.QueryDesign;

/// <summary>Reads and writes .qdesign files (JSON).</summary>
public static class QueryDesignSerializer
{
    public const string FileExtension = ".qdesign";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(QueryDesignDocument design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return JsonSerializer.Serialize(design, Options);
    }

    /// <summary>Reads a design. Raises <see cref="JsonException"/> for text that is not a design, or a newer version.</summary>
    public static QueryDesignDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var design = JsonSerializer.Deserialize<QueryDesignDocument>(json, Options)
            ?? throw new JsonException("The file is empty.");
        if (design.Version > QueryDesignDocument.CurrentVersion)
        {
            throw new JsonException($"The design was saved by a newer version (format {design.Version}).");
        }

        design.Tables ??= [];
        design.Joins ??= [];
        design.Fields ??= [];
        foreach (var field in design.Fields)
        {
            field.Or ??= [];
        }

        return design;
    }

    public static async Task<QueryDesignDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    /// <summary>Saves atomically (temp file, then replace).</summary>
    public static Task SaveAsync(string path, QueryDesignDocument design, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(path, Serialize(design), new UTF8Encoding(false), cancellationToken);
}
