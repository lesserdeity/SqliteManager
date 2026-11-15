using System.Text;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Design;

public enum AlterKind
{
    /// <summary>The design matches the database.</summary>
    None,
    Create,

    /// <summary>ALTER TABLE statements (and index changes).</summary>
    Native,

    /// <summary>The table rebuild: create, copy, drop, rename.</summary>
    Rebuild,

    /// <summary>Other schema changes (drop, rename, view or trigger edits) run in one transaction.</summary>
    Script,
}

public enum PlanStepKind
{
    Sql,
    ForeignKeysOff,
    Begin,

    /// <summary>Saves the table's triggers and the views/triggers that use it, then drops the dependent ones. <see cref="PlanStep.Sql"/> is the table name.</summary>
    SaveAndDropDependents,

    /// <summary>Re-creates what <see cref="SaveAndDropDependents"/> saved.</summary>
    RecreateDependents,
    ForeignKeyCheck,
    Commit,
    RestoreForeignKeys,
}

public sealed record PlanStep(PlanStepKind Kind, string Sql, IReadOnlyDictionary<string, object?>? Parameters = null);

/// <summary>What the designer will run. <see cref="ToScript"/> is the text shown in the preview.</summary>
public sealed record AlterPlan(
    AlterKind Kind,
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string> DroppedColumns,
    IReadOnlyList<string> DroppedIndexes,
    string? RebuildReason = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? DependentNames = null)
{
    public static AlterPlan Empty { get; } = new(AlterKind.None, [], [], []);

    public string ToScript()
    {
        var builder = new StringBuilder();
        var dependents = DependentNames is { Count: > 0 } ? string.Join(", ", DependentNames) : "none";
        foreach (var step in Steps)
        {
            var line = step.Kind switch
            {
                PlanStepKind.ForeignKeysOff => "PRAGMA foreign_keys = OFF;",
                PlanStepKind.Begin => "BEGIN;",
                PlanStepKind.SaveAndDropDependents => $"-- Save the triggers of {SqlIdentifier.Quote(step.Sql)}, and save then drop the views and triggers that use it ({dependents}).",
                PlanStepKind.RecreateDependents => "-- Re-create the saved views and triggers.",
                PlanStepKind.ForeignKeyCheck => "PRAGMA foreign_key_check;  -- roll back if this finds new problems",
                PlanStepKind.Commit => "COMMIT;",
                PlanStepKind.RestoreForeignKeys => "PRAGMA foreign_keys = <previous setting>;",
                _ => step.Sql.TrimEnd().TrimEnd(';') + ";" + FormatParameters(step.Parameters),
            };
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string FormatParameters(IReadOnlyDictionary<string, object?>? parameters) =>
        parameters is null || parameters.Count == 0 ? string.Empty : "  -- " + string.Join(", ", parameters.Select(p => $"{p.Key} = {p.Value ?? "NULL"}"));
}

/// <summary>What the planner needs to know about objects around a table. Used for the preview and warnings.</summary>
public sealed record TableDependencies(
    IReadOnlyList<SchemaObject> OwnTriggers,
    IReadOnlyList<SchemaObject> DependentViews,
    IReadOnlyList<SchemaObject> DependentTriggers,
    long? AutoIncrementSequence)
{
    public static TableDependencies None { get; } = new([], [], [], null);
}

/// <summary>Decides how to apply a table design: native ALTER TABLE when possible, otherwise the rebuild procedure.</summary>
public static class AlterPlanner
{
    public static AlterPlan PlanCreate(TableDefinition design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var steps = new List<PlanStep> { new(PlanStepKind.Begin, "BEGIN"), Sql(TableSqlGenerator.CreateTable(design)) };
        steps.AddRange(design.Indexes.Select(i => Sql(TableSqlGenerator.CreateIndex(i, design.Name))));
        steps.Add(new PlanStep(PlanStepKind.Commit, "COMMIT"));
        return new AlterPlan(AlterKind.Create, steps, [], []);
    }

    public static AlterPlan PlanAlter(TableDefinition original, TableDefinition design, TableDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(dependencies);

        var map = new ColumnMap(original, design);
        var warnings = Warnings(map, dependencies);
        var reason = WhyRebuild(original, design, map, out var indexChanges);
        if (reason is not null)
        {
            return PlanRebuild(original, design, dependencies, map, reason, warnings);
        }

        var steps = new List<PlanStep>();
        steps.AddRange(indexChanges.Dropped.Concat(indexChanges.Changed.Select(c => c.Old)).Select(i => Sql(TableSqlGenerator.Drop("INDEX", i.Name))));
        var tableName = original.Name;
        if (!design.Name.Equals(original.Name, StringComparison.Ordinal))
        {
            steps.Add(Sql($"ALTER TABLE {SqlIdentifier.Quote(original.Name)} RENAME TO {SqlIdentifier.Quote(design.Name)}"));
            tableName = design.Name;
        }

        steps.AddRange(RenameColumnSteps(tableName, map, original));
        var table = SqlIdentifier.Quote(tableName);
        steps.AddRange(map.Dropped.Select(c => Sql($"ALTER TABLE {table} DROP COLUMN {SqlIdentifier.Quote(c.Name)}")));
        steps.AddRange(map.Added.Select(c => Sql($"ALTER TABLE {table} ADD COLUMN {TableSqlGenerator.ColumnSql(c, includePrimaryKey: false)}")));
        steps.AddRange(indexChanges.Added.Concat(indexChanges.Changed.Select(c => c.New)).Select(i => Sql(TableSqlGenerator.CreateIndex(i, design.Name))));

        if (steps.Count == 0)
        {
            return AlterPlan.Empty;
        }

        steps.Insert(0, new PlanStep(PlanStepKind.Begin, "BEGIN"));
        steps.Add(new PlanStep(PlanStepKind.Commit, "COMMIT"));
        return new AlterPlan(AlterKind.Native, steps, map.Dropped.Select(c => c.Name).ToList(), indexChanges.Dropped.Select(i => i.Name).ToList(), null, warnings);
    }

    private static AlterPlan PlanRebuild(TableDefinition original, TableDefinition design, TableDependencies dependencies, ColumnMap map, string reason, IReadOnlyList<string> warnings)
    {
        var oldName = original.Name;
        var temp = $"{oldName}__rebuild_{Guid.NewGuid().ToString("N")[..8]}";
        var steps = new List<PlanStep>
        {
            new(PlanStepKind.ForeignKeysOff, "PRAGMA foreign_keys = OFF"),
            new(PlanStepKind.Begin, "BEGIN"),
        };

        // Rename columns natively first: SQLite then updates the triggers, views, and constraints that use them.
        steps.AddRange(RenameColumnSteps(oldName, map, original));
        steps.Add(Sql(TableSqlGenerator.CreateTable(design, temp)));

        // Copy every kept column (now under its new name). Generated columns compute their own values.
        var copied = map.Kept.Where(k => !k.New.IsGenerated).Select(k => SqlIdentifier.Quote(k.New.Name)).ToList();
        if (copied.Count > 0)
        {
            var list = string.Join(", ", copied);
            steps.Add(Sql($"INSERT INTO {SqlIdentifier.Quote(temp)} ({list}) SELECT {list} FROM {SqlIdentifier.Quote(oldName)}"));
        }

        steps.Add(new PlanStep(PlanStepKind.SaveAndDropDependents, oldName));
        steps.Add(Sql($"DROP TABLE {SqlIdentifier.Quote(oldName)}"));
        steps.Add(Sql($"ALTER TABLE {SqlIdentifier.Quote(temp)} RENAME TO {SqlIdentifier.Quote(oldName)}"));
        steps.AddRange(design.Indexes.Select(i => Sql(TableSqlGenerator.CreateIndex(i, oldName))));
        steps.Add(new PlanStep(PlanStepKind.RecreateDependents, oldName));

        // Keep the AUTOINCREMENT counter so deleted ids are never reused.
        if (dependencies.AutoIncrementSequence is { } sequence && design.Columns.Any(c => c.AutoIncrement))
        {
            steps.Add(new PlanStep(PlanStepKind.Sql,
                "UPDATE sqlite_sequence SET seq = @seq WHERE name = @name AND seq < @seq",
                new Dictionary<string, object?> { ["@seq"] = sequence, ["@name"] = oldName }));
        }

        steps.Add(new PlanStep(PlanStepKind.ForeignKeyCheck, "PRAGMA foreign_key_check"));
        if (!design.Name.Equals(oldName, StringComparison.Ordinal))
        {
            steps.Add(Sql($"ALTER TABLE {SqlIdentifier.Quote(oldName)} RENAME TO {SqlIdentifier.Quote(design.Name)}"));
        }

        steps.Add(new PlanStep(PlanStepKind.Commit, "COMMIT"));
        steps.Add(new PlanStep(PlanStepKind.RestoreForeignKeys, "PRAGMA foreign_keys"));

        var droppedIndexes = original.Indexes
            .Where(o => !design.Indexes.Any(n => n.OriginalName is not null && n.OriginalName.Equals(o.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(i => i.Name)
            .ToList();
        var dependents = dependencies.OwnTriggers.Concat(dependencies.DependentViews).Concat(dependencies.DependentTriggers).Select(o => o.Name).ToList();
        return new AlterPlan(AlterKind.Rebuild, steps, map.Dropped.Select(c => c.Name).ToList(), droppedIndexes, reason, warnings, dependents);
    }

    /// <summary>
    /// RENAME COLUMN statements. If a new name is still used by another column, every rename goes
    /// through a temporary name first (so a and b can swap names).
    /// </summary>
    private static List<PlanStep> RenameColumnSteps(string table, ColumnMap map, TableDefinition original)
    {
        var renames = map.Kept.Where(k => !k.Old.Name.Equals(k.New.Name, StringComparison.Ordinal)).ToList();
        var quotedTable = SqlIdentifier.Quote(table);
        var collides = renames.Any(r => original.Columns.Any(o => !ReferenceEquals(o, r.Old) && o.Name.Equals(r.New.Name, StringComparison.OrdinalIgnoreCase)));
        if (!collides)
        {
            return renames.Select(r => Sql($"ALTER TABLE {quotedTable} RENAME COLUMN {SqlIdentifier.Quote(r.Old.Name)} TO {SqlIdentifier.Quote(r.New.Name)}")).ToList();
        }

        var steps = new List<PlanStep>();
        for (var i = 0; i < renames.Count; i++)
        {
            steps.Add(Sql($"ALTER TABLE {quotedTable} RENAME COLUMN {SqlIdentifier.Quote(renames[i].Old.Name)} TO {SqlIdentifier.Quote(TempName(i))}"));
        }

        for (var i = 0; i < renames.Count; i++)
        {
            steps.Add(Sql($"ALTER TABLE {quotedTable} RENAME COLUMN {SqlIdentifier.Quote(TempName(i))} TO {SqlIdentifier.Quote(renames[i].New.Name)}"));
        }

        return steps;
    }

    private static string TempName(int i) => $"__rename_{i}";

    /// <summary>Returns why native ALTER TABLE cannot apply the design, or null if it can.</summary>
    private static string? WhyRebuild(TableDefinition original, TableDefinition design, ColumnMap map, out IndexChanges indexChanges)
    {
        // Compare against the original with the renames applied, as RENAME COLUMN would leave it.
        var renamed = original.Clone();
        var swap = map.Kept.Any(k => !k.Old.Name.Equals(k.New.Name, StringComparison.OrdinalIgnoreCase)
            && original.Columns.Any(o => !ReferenceEquals(o, k.Old) && o.Name.Equals(k.New.Name, StringComparison.OrdinalIgnoreCase)));
        if (!swap)
        {
            foreach (var (oldColumn, newColumn) in map.Kept)
            {
                renamed.RenameColumn(renamed.Columns[original.Columns.IndexOf(oldColumn)], newColumn.Name);
            }
        }

        indexChanges = CompareIndexes(original, renamed, design);
        if (swap)
        {
            return "Columns swap names.";
        }

        if (original.WithoutRowId != design.WithoutRowId || original.Strict != design.Strict)
        {
            return "Table options (WITHOUT ROWID, STRICT) changed.";
        }

        if (!renamed.TableConstraints.Select(Normalize).SequenceEqual(design.TableConstraints.Select(Normalize)))
        {
            return "Table constraints changed.";
        }

        if (original.PrimaryKeyConstraintName != design.PrimaryKeyConstraintName || original.PrimaryKeyConflict != design.PrimaryKeyConflict)
        {
            return "The primary key constraint changed.";
        }

        var lastKeptIndex = -1;
        var addedSeen = false;
        foreach (var column in design.Columns)
        {
            var kept = map.Kept.FirstOrDefault(k => ReferenceEquals(k.New, column));
            if (kept.New is null)
            {
                addedSeen = true;
                continue;
            }

            var oldIndex = original.Columns.IndexOf(kept.Old);
            if (addedSeen || oldIndex < lastKeptIndex)
            {
                return "Columns were reordered or added between existing columns.";
            }

            lastKeptIndex = oldIndex;
        }

        var originalColumnLevelPk = TableSqlGenerator.UsesColumnLevelPrimaryKey(renamed);
        var designColumnLevelPk = TableSqlGenerator.UsesColumnLevelPrimaryKey(design);
        foreach (var (oldColumn, newColumn) in map.Kept)
        {
            var before = renamed.Columns[original.Columns.IndexOf(oldColumn)];
            if (before.PrimaryKeyOrder != newColumn.PrimaryKeyOrder
                || TableSqlGenerator.ColumnSql(before, originalColumnLevelPk && before.IsPrimaryKey, renamed)
                != TableSqlGenerator.ColumnSql(newColumn, designColumnLevelPk && newColumn.IsPrimaryKey, design))
            {
                return $"Column \"{newColumn.Name}\" changed in a way ALTER TABLE cannot do.";
            }
        }

        if (!renamed.ForeignKeys.Select(TableSqlGenerator.ForeignKeySql).SequenceEqual(design.ForeignKeys.Select(TableSqlGenerator.ForeignKeySql)))
        {
            return "Foreign keys changed.";
        }

        foreach (var column in map.Added)
        {
            var defaultText = column.Default?.Trim() ?? string.Empty;
            if (column.IsPrimaryKey || column.Unique)
            {
                return $"New column \"{column.Name}\" is a primary key or UNIQUE.";
            }

            if (column.NotNull && (defaultText.Length == 0 || defaultText.Equals("NULL", StringComparison.OrdinalIgnoreCase)))
            {
                return $"New column \"{column.Name}\" is NOT NULL without a default.";
            }

            if (defaultText.StartsWith('(') || defaultText.StartsWith("CURRENT_", StringComparison.OrdinalIgnoreCase))
            {
                return $"New column \"{column.Name}\" has a default that ALTER TABLE ADD COLUMN does not allow.";
            }

            if (column.Extra is not null && SqlTokenizer.Tokenize(column.Extra).Any(t => t.Is("STORED")))
            {
                return $"New column \"{column.Name}\" is a STORED generated column.";
            }
        }

        foreach (var column in map.Dropped)
        {
            if (column.IsPrimaryKey || column.Unique)
            {
                return $"Dropped column \"{column.Name}\" is a primary key or UNIQUE.";
            }

            if (original.ForeignKeys.Any(fk => fk.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)))
            {
                return $"Dropped column \"{column.Name}\" is part of a foreign key.";
            }

            if (indexChanges.Unchanged.Any(i => i.Terms.Any(t => Mentions(t.Column ?? t.Expression, column.Name)) || Mentions(i.Where, column.Name)))
            {
                return $"Dropped column \"{column.Name}\" is used by an index.";
            }

            if (original.TableConstraints.Any(t => Mentions(t, column.Name))
                || original.Columns.Any(c => !ReferenceEquals(c, column) && (Mentions(c.Check, column.Name) || Mentions(c.Extra, column.Name))))
            {
                return $"Dropped column \"{column.Name}\" is used by a constraint or generated column.";
            }
        }

        return null;
    }

    /// <summary>Warns about triggers and views that use a column being dropped; they may stop working.</summary>
    private static List<string> Warnings(ColumnMap map, TableDependencies dependencies)
    {
        var warnings = new List<string>();
        var users = dependencies.OwnTriggers.Concat(dependencies.DependentViews).Concat(dependencies.DependentTriggers).ToList();
        foreach (var column in map.Dropped)
        {
            foreach (var user in users.Where(u => Mentions(u.Sql, column.Name)))
            {
                warnings.Add($"{(user.Type == SchemaObjectType.View ? "View" : "Trigger")} \"{user.Name}\" uses \"{column.Name}\", which is being dropped. It may stop working.");
            }
        }

        return warnings;
    }

    /// <param name="renamedOriginal">The original with the design's column renames applied.</param>
    private static IndexChanges CompareIndexes(TableDefinition original, TableDefinition renamedOriginal, TableDefinition design)
    {
        var changes = new IndexChanges();
        for (var i = 0; i < original.Indexes.Count; i++)
        {
            var oldIndex = original.Indexes[i];
            var newIndex = design.Indexes.FirstOrDefault(n => n.OriginalName is not null && n.OriginalName.Equals(oldIndex.Name, StringComparison.OrdinalIgnoreCase));
            if (newIndex is null)
            {
                changes.Dropped.Add(oldIndex);
            }
            else if (TableSqlGenerator.CreateIndex(renamedOriginal.Indexes[i], design.Name) == TableSqlGenerator.CreateIndex(newIndex, design.Name))
            {
                changes.Unchanged.Add(oldIndex);
            }
            else
            {
                changes.Changed.Add((oldIndex, newIndex));
            }
        }

        changes.Added.AddRange(design.Indexes.Where(n => n.OriginalName is null || !original.Indexes.Any(o => o.Name.Equals(n.OriginalName, StringComparison.OrdinalIgnoreCase))));
        return changes;
    }

    /// <summary>True if <paramref name="sql"/> contains <paramref name="name"/> as a name token.</summary>
    internal static bool Mentions(string? sql, string name)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        try
        {
            return SqlTokenizer.Tokenize(sql).Any(t => t.IsName && t.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (SqlParseException)
        {
            return true;
        }
    }

    private static string Normalize(string sql) => string.Join(" ", sql.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static PlanStep Sql(string sql) => new(PlanStepKind.Sql, sql);

    private sealed class IndexChanges
    {
        public List<IndexDefinition> Dropped { get; } = [];

        public List<IndexDefinition> Added { get; } = [];

        public List<(IndexDefinition Old, IndexDefinition New)> Changed { get; } = [];

        public List<IndexDefinition> Unchanged { get; } = [];
    }

    /// <summary>Matches design columns to original columns by <see cref="ColumnDefinition.OriginalName"/>.</summary>
    private sealed class ColumnMap
    {
        public ColumnMap(TableDefinition original, TableDefinition design)
        {
            foreach (var column in design.Columns)
            {
                var old = column.OriginalName is null ? null : original.FindColumn(column.OriginalName);
                if (old is null || Kept.Any(k => ReferenceEquals(k.Old, old)))
                {
                    Added.Add(column);
                }
                else
                {
                    Kept.Add((old, column));
                }
            }

            Dropped.AddRange(original.Columns.Where(o => !Kept.Any(k => ReferenceEquals(k.Old, o))));
        }

        public List<(ColumnDefinition Old, ColumnDefinition New)> Kept { get; } = [];

        public List<ColumnDefinition> Added { get; } = [];

        public List<ColumnDefinition> Dropped { get; } = [];
    }
}
