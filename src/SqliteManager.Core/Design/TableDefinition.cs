namespace SqliteManager.Core.Design;

/// <summary>Column constraints whose CONSTRAINT name or ON CONFLICT clause is kept.</summary>
public enum ColumnConstraintKind
{
    PrimaryKey,
    NotNull,
    Unique,
    Check,
    Default,
    Collate,
}

/// <summary>One column in the table designer.</summary>
public sealed class ColumnDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The column's name in the database, or null for a new column. Used to copy its data.</summary>
    public string? OriginalName { get; set; }

    /// <summary>The declared type as written, for example INTEGER or VARCHAR(20). May be empty.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Position in the primary key (1-based), or 0 if not part of it.</summary>
    public int PrimaryKeyOrder { get; set; }

    public bool PrimaryKeyDescending { get; set; }

    public bool AutoIncrement { get; set; }

    public bool NotNull { get; set; }

    public bool Unique { get; set; }

    /// <summary>The DEFAULT value as SQL, for example 'n/a', 0, CURRENT_TIMESTAMP, or (expression).</summary>
    public string? Default { get; set; }

    /// <summary>The CHECK expression (without the parentheses).</summary>
    public string? Check { get; set; }

    public string? Collation { get; set; }

    /// <summary>Other constraints kept as written, for example GENERATED ALWAYS AS (...) STORED.</summary>
    public string? Extra { get; set; }

    /// <summary>CONSTRAINT names written before a constraint.</summary>
    public Dictionary<ColumnConstraintKind, string> ConstraintNames { get; private set; } = [];

    /// <summary>ON CONFLICT clauses after a constraint.</summary>
    public Dictionary<ColumnConstraintKind, string> ConflictClauses { get; private set; } = [];

    public bool IsPrimaryKey => PrimaryKeyOrder > 0;

    /// <summary>True for generated columns (GENERATED ALWAYS AS / AS).</summary>
    public bool IsGenerated =>
        !string.IsNullOrWhiteSpace(Extra) && SqlTokenizer.Tokenize(Extra).Any(t => t.Is("AS"));

    public ColumnDefinition Clone()
    {
        var copy = (ColumnDefinition)MemberwiseClone();
        copy.ConstraintNames = new Dictionary<ColumnConstraintKind, string>(ConstraintNames);
        copy.ConflictClauses = new Dictionary<ColumnConstraintKind, string>(ConflictClauses);
        return copy;
    }
}

/// <summary>One term of an index: a column (with optional COLLATE and DESC) or an expression kept as written.</summary>
public sealed record IndexTerm(string? Column, string? Expression, string? Collation = null, bool Descending = false)
{
    public static IndexTerm ForColumn(string column, bool descending = false) => new(column, null, null, descending);
}

/// <summary>An index created with CREATE INDEX.</summary>
public sealed class IndexDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The index name in the database, or null for a new index.</summary>
    public string? OriginalName { get; set; }

    public bool Unique { get; set; }

    public List<IndexTerm> Terms { get; set; } = [];

    /// <summary>The WHERE expression of a partial index.</summary>
    public string? Where { get; set; }

    public IndexDefinition Clone()
    {
        var copy = (IndexDefinition)MemberwiseClone();
        copy.Terms = [.. Terms];
        return copy;
    }
}

/// <summary>A FOREIGN KEY constraint (column-level REFERENCES are read into this form too).</summary>
public sealed class ForeignKeyDefinition
{
    public List<string> Columns { get; set; } = [];

    public string Table { get; set; } = string.Empty;

    /// <summary>Referenced columns. Empty means the referenced table's primary key.</summary>
    public List<string> ReferencedColumns { get; set; } = [];

    /// <summary>ON UPDATE action, for example CASCADE, or null.</summary>
    public string? OnUpdate { get; set; }

    public string? OnDelete { get; set; }

    /// <summary>Other clauses kept as written, for example DEFERRABLE INITIALLY DEFERRED.</summary>
    public string? Extra { get; set; }

    public string? ConstraintName { get; set; }

    public ForeignKeyDefinition Clone()
    {
        var copy = (ForeignKeyDefinition)MemberwiseClone();
        copy.Columns = [.. Columns];
        copy.ReferencedColumns = [.. ReferencedColumns];
        return copy;
    }
}

/// <summary>A table as edited in the designer.</summary>
public sealed class TableDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<ColumnDefinition> Columns { get; set; } = [];

    public List<IndexDefinition> Indexes { get; set; } = [];

    public List<ForeignKeyDefinition> ForeignKeys { get; set; } = [];

    /// <summary>Table-level UNIQUE and CHECK constraints kept as written.</summary>
    public List<string> TableConstraints { get; set; } = [];

    public bool WithoutRowId { get; set; }

    public bool Strict { get; set; }

    /// <summary>CONSTRAINT name of a table-level PRIMARY KEY.</summary>
    public string? PrimaryKeyConstraintName { get; set; }

    /// <summary>ON CONFLICT clause of a table-level PRIMARY KEY.</summary>
    public string? PrimaryKeyConflict { get; set; }

    public IReadOnlyList<ColumnDefinition> PrimaryKeyColumns =>
        Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.PrimaryKeyOrder).ToList();

    public ColumnDefinition? FindColumn(string name) =>
        Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public TableDefinition Clone() => new()
    {
        Name = Name,
        Columns = Columns.Select(c => c.Clone()).ToList(),
        Indexes = Indexes.Select(i => i.Clone()).ToList(),
        ForeignKeys = ForeignKeys.Select(f => f.Clone()).ToList(),
        TableConstraints = [.. TableConstraints],
        WithoutRowId = WithoutRowId,
        Strict = Strict,
        PrimaryKeyConstraintName = PrimaryKeyConstraintName,
        PrimaryKeyConflict = PrimaryKeyConflict,
    };

    /// <summary>
    /// Renames a column and updates everything in the design that uses the old name: index terms and
    /// WHERE clauses, foreign keys, CHECK expressions, generated columns, and table constraints.
    /// </summary>
    public void RenameColumn(ColumnDefinition column, string newName)
    {
        var oldName = column.Name;
        column.Name = newName;
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName.Equals(newName, StringComparison.Ordinal))
        {
            return;
        }

        string? Rewrite(string? sql) => sql is null ? null : SqlRewriter.RenameIdentifier(sql, oldName, newName);

        foreach (var c in Columns)
        {
            c.Check = Rewrite(c.Check);
            c.Extra = Rewrite(c.Extra);
            if (c.Default is { } d && d.TrimStart().StartsWith('('))
            {
                c.Default = Rewrite(d);
            }
        }

        TableConstraints = TableConstraints.Select(t => Rewrite(t)!).ToList();
        foreach (var index in Indexes)
        {
            index.Terms = index.Terms
                .Select(t => t.Column is not null && t.Column.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? t with { Column = newName }
                    : t.Expression is not null ? t with { Expression = Rewrite(t.Expression) }
                    : t)
                .ToList();
            index.Where = Rewrite(index.Where);
        }

        foreach (var fk in ForeignKeys)
        {
            fk.Columns = fk.Columns.Select(c => c.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : c).ToList();
        }
    }
}
