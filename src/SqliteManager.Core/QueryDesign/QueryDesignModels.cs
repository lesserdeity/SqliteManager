namespace SqliteManager.Core.QueryDesign;

public enum JoinKind
{
    /// <summary>Only rows that match in both tables.</summary>
    Inner,

    /// <summary>All rows of the left table, and the matching rows of the right table.</summary>
    Left,

    /// <summary>Every row of one table combined with every row of the other (no ON condition).</summary>
    Cross,
}

public enum SortDirection
{
    None,
    Ascending,
    Descending,
}

/// <summary>The Total row of a field in Totals mode.</summary>
public enum TotalKind
{
    GroupBy,
    Count,
    Sum,
    Avg,
    Min,
    Max,

    /// <summary>The field is an expression that already contains its aggregate. Not grouped.</summary>
    Expression,

    /// <summary>The field is used only for its criteria, which go to WHERE (before grouping). Never shown.</summary>
    Where,
}

/// <summary>A table (or view) placed on the canvas. <see cref="Alias"/> is unique in the design.</summary>
public sealed class DesignTable
{
    public string Alias { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>
/// A join line between two columns. For <see cref="JoinKind.Left"/>, all rows of
/// <see cref="LeftTable"/> are kept. Several joins between the same two tables are combined with AND.
/// </summary>
public sealed class DesignJoin
{
    public string LeftTable { get; set; } = string.Empty;

    public string LeftColumn { get; set; } = string.Empty;

    public string RightTable { get; set; } = string.Empty;

    public string RightColumn { get; set; } = string.Empty;

    public JoinKind Kind { get; set; } = JoinKind.Inner;

    public bool Connects(string a, string b) =>
        (Same(LeftTable, a) && Same(RightTable, b)) || (Same(LeftTable, b) && Same(RightTable, a));

    public bool Touches(string alias) => Same(LeftTable, alias) || Same(RightTable, alias);

    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A row of the criteria grid. With <see cref="Table"/> set, <see cref="Field"/> is a column name of that
/// table (or <c>*</c>). Without a table, <see cref="Field"/> is an SQL expression written as typed.
/// </summary>
public sealed class DesignField
{
    public string Field { get; set; } = string.Empty;

    public string? Table { get; set; }

    public string? Alias { get; set; }

    public bool Show { get; set; } = true;

    public SortDirection Sort { get; set; }

    /// <summary>Position in ORDER BY (lowest first). Fields without one follow, in grid order.</summary>
    public int? SortOrder { get; set; }

    public TotalKind Total { get; set; }

    /// <summary>The first criteria row.</summary>
    public string? Criteria { get; set; }

    /// <summary>The "or" criteria rows. Row n of every field is combined with AND; rows are combined with OR.</summary>
    public List<string?> Or { get; set; } = [];

    /// <summary>Criteria row <paramref name="row"/> (0 = <see cref="Criteria"/>).</summary>
    public string? CriteriaRow(int row) => row == 0 ? Criteria : row - 1 < Or.Count ? Or[row - 1] : null;

    public bool IsColumnOf(string alias, string column) =>
        DesignJoin.Same(Table, alias) && string.Equals(Field, column, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An Access-style query design, saved as a .qdesign JSON file.</summary>
public sealed class QueryDesignDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The database the design was made for. Informational; a design can be opened on any database.</summary>
    public string? Database { get; set; }

    public List<DesignTable> Tables { get; set; } = [];

    public List<DesignJoin> Joins { get; set; } = [];

    public List<DesignField> Fields { get; set; } = [];

    /// <summary>Totals mode: the Total row (group by / aggregates) is used.</summary>
    public bool Totals { get; set; }

    /// <summary>SELECT DISTINCT.</summary>
    public bool Distinct { get; set; }

    /// <summary>LIMIT, or null for all rows.</summary>
    public int? Limit { get; set; }

    public DesignTable? FindTable(string? alias) =>
        alias is null ? null : Tables.FirstOrDefault(t => DesignJoin.Same(t.Alias, alias));

    /// <summary>A free alias for another copy of <paramref name="table"/>: the name, then name_1, name_2, ...</summary>
    public string NewAlias(string table)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        if (FindTable(table) is null)
        {
            return table;
        }

        for (var i = 1; ; i++)
        {
            var alias = $"{table}_{i}";
            if (FindTable(alias) is null)
            {
                return alias;
            }
        }
    }

    /// <summary>Adds a table at a position and returns it (with a unique alias).</summary>
    public DesignTable AddTable(string table, int x, int y)
    {
        var added = new DesignTable { Alias = NewAlias(table), Table = table, X = x, Y = y };
        Tables.Add(added);
        return added;
    }

    /// <summary>Removes a table with its joins and fields.</summary>
    public void RemoveTable(string alias)
    {
        Tables.RemoveAll(t => DesignJoin.Same(t.Alias, alias));
        Joins.RemoveAll(j => j.Touches(alias));
        Fields.RemoveAll(f => DesignJoin.Same(f.Table, alias));
    }

    /// <summary>Adds a join unless the same column pair is already joined. Returns the join that connects them.</summary>
    public DesignJoin AddJoin(string leftTable, string leftColumn, string rightTable, string rightColumn, JoinKind kind = JoinKind.Inner)
    {
        var existing = Joins.FirstOrDefault(j =>
            (DesignJoin.Same(j.LeftTable, leftTable) && DesignJoin.Same(j.LeftColumn, leftColumn) && DesignJoin.Same(j.RightTable, rightTable) && DesignJoin.Same(j.RightColumn, rightColumn))
            || (DesignJoin.Same(j.LeftTable, rightTable) && DesignJoin.Same(j.LeftColumn, rightColumn) && DesignJoin.Same(j.RightTable, leftTable) && DesignJoin.Same(j.RightColumn, leftColumn)));
        if (existing is not null)
        {
            return existing;
        }

        // A second join between the same two tables uses the type (and direction) of the first one.
        var sibling = Joins.FirstOrDefault(j => j.Connects(leftTable, rightTable));
        var join = new DesignJoin { LeftTable = leftTable, LeftColumn = leftColumn, RightTable = rightTable, RightColumn = rightColumn, Kind = kind };
        if (sibling is not null)
        {
            join.Kind = sibling.Kind;
            if (!DesignJoin.Same(sibling.LeftTable, leftTable))
            {
                (join.LeftTable, join.LeftColumn, join.RightTable, join.RightColumn) = (rightTable, rightColumn, leftTable, leftColumn);
            }
        }

        Joins.Add(join);
        return join;
    }

    /// <summary>
    /// Sets the type of every join between the two tables of <paramref name="join"/>. With
    /// <paramref name="keepAllFrom"/> (for <see cref="JoinKind.Left"/>), the joins are turned so that table is on the left.
    /// </summary>
    public void SetJoinKind(DesignJoin join, JoinKind kind, string? keepAllFrom = null)
    {
        ArgumentNullException.ThrowIfNull(join);
        foreach (var j in Joins.Where(j => j.Connects(join.LeftTable, join.RightTable)).ToList())
        {
            j.Kind = kind;
            if (kind == JoinKind.Left && keepAllFrom is not null && !DesignJoin.Same(j.LeftTable, keepAllFrom))
            {
                (j.LeftTable, j.LeftColumn, j.RightTable, j.RightColumn) = (j.RightTable, j.RightColumn, j.LeftTable, j.LeftColumn);
            }
        }
    }

    /// <summary>Number of criteria rows in use (at least 1).</summary>
    public int CriteriaRowCount => Math.Max(1, Fields.Count == 0 ? 1 : Fields.Max(f => f.Or.Count + 1));
}
