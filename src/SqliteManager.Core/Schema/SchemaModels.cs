namespace SqliteManager.Core.Schema;

public enum SchemaObjectType
{
    Table,
    View,
    Index,
    Trigger,
}

/// <summary>A row of <c>sqlite_schema</c>.</summary>
public sealed record SchemaObject(string Name, SchemaObjectType Type, string TableName, string? Sql)
{
    /// <summary>True for objects that SQLite creates itself, such as <c>sqlite_sequence</c> or automatic indexes.</summary>
    public bool IsSystem => Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
}

public sealed record ColumnInfo(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool NotNull,
    string? DefaultValue,
    int PrimaryKeyOrdinal,
    bool IsGenerated)
{
    public bool IsPrimaryKey => PrimaryKeyOrdinal > 0;
}

public sealed record ForeignKeyInfo(
    int Id,
    string ReferencedTable,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string?> ToColumns,
    string OnUpdate,
    string OnDelete);

/// <summary>An index on a table. <see cref="Origin"/> is "c" (CREATE INDEX), "u" (UNIQUE constraint) or "pk".</summary>
public sealed record IndexInfo(
    string Name,
    bool IsUnique,
    string Origin,
    bool IsPartial,
    IReadOnlyList<string?> Columns);

/// <summary>Table, view, and column names of one attached database (ATTACH DATABASE ... AS <see cref="Name"/>).</summary>
public sealed record AttachedSchema(
    string Name,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Views,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Columns);

/// <summary>
/// Names from the live schema, used for autocomplete. <see cref="Tables"/>, <see cref="Views"/>, and
/// <see cref="Columns"/> describe the main database; <see cref="Attached"/> lists attached databases in attach order.
/// Name lookups ignore case.
/// </summary>
public sealed record SchemaSnapshot(
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Views,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Columns,
    IReadOnlyList<string> Functions,
    IReadOnlyList<AttachedSchema>? Attached = null)
{
    public static SchemaSnapshot Empty { get; } = new([], [], new Dictionary<string, IReadOnlyList<string>>(), []);

    /// <summary>Attached databases; never null.</summary>
    public IReadOnlyList<AttachedSchema> AttachedSchemas => Attached ?? [];

    /// <summary>The attached database with this schema name, or null. main and temp are not attached databases.</summary>
    public AttachedSchema? FindAttached(string name) =>
        AttachedSchemas.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public enum TableKind
{
    Table,
    View,
    Virtual,
    Shadow,
}

public sealed record TableDetails(
    string Name,
    TableKind Kind,
    bool HasRowId,
    bool IsStrict,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<IndexInfo> Indexes)
{
    public IReadOnlyList<ColumnInfo> PrimaryKey =>
        Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.PrimaryKeyOrdinal).ToArray();
}
