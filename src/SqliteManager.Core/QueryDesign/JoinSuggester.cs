using SqliteManager.Core.Schema;

namespace SqliteManager.Core.QueryDesign;

/// <summary>Finds the joins to draw when a table is added to a design, from the foreign keys of the tables.</summary>
public static class JoinSuggester
{
    /// <summary>
    /// Joins between <paramref name="added"/> and the other tables of <paramref name="design"/>, in both directions.
    /// A foreign key without target columns refers to the primary key of the referenced table.
    /// Joins already in the design are not returned.
    /// </summary>
    /// <param name="details">Table details by table name (ignoring case). Tables without details are skipped.</param>
    public static IReadOnlyList<DesignJoin> Suggest(QueryDesignDocument design, DesignTable added, Func<string, TableDetails?> details)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(details);

        var result = new List<DesignJoin>();
        var addedDetails = details(added.Table);
        if (addedDetails is null)
        {
            return result;
        }

        foreach (var other in design.Tables.Where(t => !ReferenceEquals(t, added) && !DesignJoin.Same(t.Alias, added.Alias)))
        {
            var otherDetails = details(other.Table);
            if (otherDetails is null)
            {
                continue;
            }

            // Two copies of the same table are only joined by a self-referencing key from the new copy.
            AddFrom(added, addedDetails, other, otherDetails, result);
            if (!DesignJoin.Same(other.Table, added.Table))
            {
                AddFrom(other, otherDetails, added, addedDetails, result);
            }
        }

        return result
            .Where(j => !design.Joins.Any(e => SameColumns(e, j)))
            .ToList();
    }

    /// <summary>Joins for the foreign keys of <paramref name="child"/> that refer to <paramref name="parent"/>'s table.</summary>
    private static void AddFrom(DesignTable child, TableDetails childDetails, DesignTable parent, TableDetails parentDetails, List<DesignJoin> result)
    {
        foreach (var fk in childDetails.ForeignKeys.Where(f => DesignJoin.Same(f.ReferencedTable, parent.Table)))
        {
            var targets = fk.ToColumns.All(c => c is null)
                ? parentDetails.PrimaryKey.Select(c => c.Name).ToArray()
                : fk.ToColumns.Select(c => c ?? string.Empty).ToArray();
            if (targets.Length != fk.FromColumns.Count || targets.Any(string.IsNullOrEmpty))
            {
                continue;
            }

            for (var i = 0; i < targets.Length; i++)
            {
                var join = new DesignJoin
                {
                    LeftTable = parent.Alias,
                    LeftColumn = targets[i],
                    RightTable = child.Alias,
                    RightColumn = fk.FromColumns[i],
                };
                if (!result.Any(r => SameColumns(r, join)))
                {
                    result.Add(join);
                }
            }
        }
    }

    private static bool SameColumns(DesignJoin a, DesignJoin b) =>
        (DesignJoin.Same(a.LeftTable, b.LeftTable) && DesignJoin.Same(a.LeftColumn, b.LeftColumn) && DesignJoin.Same(a.RightTable, b.RightTable) && DesignJoin.Same(a.RightColumn, b.RightColumn))
        || (DesignJoin.Same(a.LeftTable, b.RightTable) && DesignJoin.Same(a.LeftColumn, b.RightColumn) && DesignJoin.Same(a.RightTable, b.LeftTable) && DesignJoin.Same(a.RightColumn, b.LeftColumn));
}
