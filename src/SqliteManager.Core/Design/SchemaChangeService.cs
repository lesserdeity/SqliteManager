using System.Globalization;
using SqliteManager.Core.Data;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Design;

/// <summary>A table loaded into the designer, with what the planner needs to know about the database.</summary>
public sealed record TableDesign(TableDefinition Definition, TableDependencies Dependencies, long RowCount);

/// <summary>The rebuild found foreign-key problems that were not there before, so nothing was changed.</summary>
public sealed class ForeignKeyViolationException(IReadOnlyList<string> violations)
    : Exception("The change would break foreign keys, so it was rolled back:\n" + string.Join("\n", violations.Take(20)) +
                (violations.Count > 20 ? $"\n… and {violations.Count - 20} more." : string.Empty))
{
    public IReadOnlyList<string> Violations { get; } = violations;
}

/// <summary>Loads designs from the database and applies schema-change plans.</summary>
public sealed class SchemaChangeService
{
    private readonly DatabaseSession _session;

    public SchemaChangeService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Reads a table's definition, indexes, dependent objects, row count, and AUTOINCREMENT counter.</summary>
    public async Task<TableDesign> LoadTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        var objects = await new SchemaService(_session).GetObjectsAsync(cancellationToken).ConfigureAwait(false);
        var table = objects.FirstOrDefault(o => o.Type == SchemaObjectType.Table && o.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The table \"{tableName}\" does not exist.", nameof(tableName));
        if (table.Sql is null)
        {
            throw new SqlParseException($"\"{tableName}\" is an internal table and cannot be designed.");
        }

        var definition = CreateTableParser.Parse(table.Sql);
        definition.Name = table.Name;
        foreach (var index in objects.Where(o => o.Type == SchemaObjectType.Index && o.Sql is not null && o.TableName.Equals(table.Name, StringComparison.OrdinalIgnoreCase)))
        {
            definition.Indexes.Add(IndexParser.Parse(index.Sql!));
        }

        var (ownTriggers, dependents) = FindDependents(table.Name, objects);
        var dependentViews = dependents.Where(o => o.Type == SchemaObjectType.View).ToList();
        var dependentTriggers = dependents.Where(o => o.Type == SchemaObjectType.Trigger).ToList();

        long? sequence = null;
        if (objects.Any(o => o.Name == "sqlite_sequence"))
        {
            sequence = await _session.ExecuteScalarAsync<long?>(
                "SELECT seq FROM sqlite_sequence WHERE name = @name",
                new Dictionary<string, object?> { ["@name"] = table.Name }, cancellationToken).ConfigureAwait(false);
        }

        var rowCount = await _session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {SqlIdentifier.Quote(table.Name)}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new TableDesign(definition, new TableDependencies(ownTriggers, dependentViews, dependentTriggers, sequence), rowCount);
    }

    /// <summary>
    /// Runs a plan. Refuses to start inside an open transaction. Any failure rolls everything back.
    /// For a rebuild, foreign keys are switched off during the change and restored afterwards, and
    /// <see cref="ForeignKeyViolationException"/> is raised if the change introduces new violations.
    /// </summary>
    public Task ApplyAsync(AlterPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Steps.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _session.RunExclusiveAsync(runner =>
        {
            if (runner.IsInTransaction)
            {
                throw new InvalidOperationException(
                    "A transaction is open on this database (for example a BEGIN in a query tab). Run COMMIT or ROLLBACK first, then try again.");
            }

            long? foreignKeys = null;
            HashSet<string>? baseline = null;
            List<string> saved = [];
            var begun = false;
            try
            {
                foreach (var step in plan.Steps)
                {
                    switch (step.Kind)
                    {
                        case PlanStepKind.ForeignKeysOff:
                            foreignKeys = Convert.ToInt64(runner.Query("PRAGMA foreign_keys")[0][0], CultureInfo.InvariantCulture);
                            baseline = ForeignKeyProblems(runner);
                            runner.Execute("PRAGMA foreign_keys = OFF");
                            break;
                        case PlanStepKind.Begin:
                            runner.Execute("BEGIN");
                            begun = true;
                            break;
                        case PlanStepKind.SaveAndDropDependents:
                            saved = SaveAndDropDependents(runner, step.Sql);
                            break;
                        case PlanStepKind.RecreateDependents:
                            foreach (var sql in saved)
                            {
                                runner.Execute(sql);
                            }

                            break;
                        case PlanStepKind.ForeignKeyCheck:
                            var added = ForeignKeyProblems(runner);
                            added.ExceptWith(baseline ?? []);
                            if (added.Count > 0)
                            {
                                throw new ForeignKeyViolationException(added.Order().ToList());
                            }

                            break;
                        case PlanStepKind.Commit:
                            runner.Execute("COMMIT");
                            begun = false;
                            break;
                        case PlanStepKind.RestoreForeignKeys:
                            break; // Done in finally.
                        default:
                            runner.Execute(step.Sql, step.Parameters);
                            break;
                    }
                }
            }
            catch
            {
                if (begun && runner.IsInTransaction)
                {
                    runner.Execute("ROLLBACK");
                }

                throw;
            }
            finally
            {
                if (foreignKeys is { } previous)
                {
                    runner.Execute($"PRAGMA foreign_keys = {(previous != 0 ? "ON" : "OFF")}");
                }
            }

            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Reads the current schema, drops the views and other tables' triggers that use <paramref name="table"/>
    /// (directly or through another view), and returns the SQL to re-create them and the table's own triggers,
    /// in an order that works: views first (each after the views it uses), then triggers.
    /// </summary>
    private static List<string> SaveAndDropDependents(ISqlRunner runner, string table)
    {
        var objects = runner.Query("SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE type IN ('view', 'trigger') AND sql IS NOT NULL ORDER BY rowid")
            .Select(r => new SchemaObject((string)r[1]!, (string)r[0]! == "view" ? SchemaObjectType.View : SchemaObjectType.Trigger, (string)r[2]!, (string?)r[3]))
            .ToList();
        var (ownTriggers, dependents) = FindDependents(table, objects);
        foreach (var dependent in dependents)
        {
            runner.Execute(TableSqlGenerator.Drop(dependent.Type == SchemaObjectType.View ? "VIEW" : "TRIGGER", dependent.Name));
        }

        return dependents.Where(d => d.Type == SchemaObjectType.View)
            .Concat(ownTriggers)
            .Concat(dependents.Where(d => d.Type == SchemaObjectType.Trigger))
            .Select(o => o.Sql!)
            .ToList();
    }

    /// <summary>
    /// The table's own triggers, and the views and other triggers that use the table, directly or through
    /// views that use it. Views are listed after the views they use.
    /// </summary>
    public static (List<SchemaObject> OwnTriggers, List<SchemaObject> Dependents) FindDependents(string table, IReadOnlyList<SchemaObject> objects)
    {
        var ownTriggers = objects.Where(o => o.Type == SchemaObjectType.Trigger && o.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
        var names = new List<string> { table };
        var dependents = new List<SchemaObject>();
        bool added;
        do
        {
            added = false;
            foreach (var obj in objects)
            {
                if (obj.Type is not (SchemaObjectType.View or SchemaObjectType.Trigger) || ownTriggers.Contains(obj) || dependents.Contains(obj)
                    || !names.Any(n => Mentions(obj.Sql, n)))
                {
                    continue;
                }

                dependents.Add(obj);
                if (obj.Type == SchemaObjectType.View)
                {
                    names.Add(obj.Name);
                }

                added = true;
            }
        }
        while (added);

        return (ownTriggers, dependents);
    }

    private static HashSet<string> ForeignKeyProblems(ISqlRunner runner) =>
        runner.Query("PRAGMA foreign_key_check")
            .Select(r => $"Row {r[1] ?? "?"} in \"{r[0]}\" has no matching row in \"{r[2]}\" (foreign key {r[3]}).")
            .ToHashSet();

    /// <summary>True if <paramref name="sql"/> uses <paramref name="name"/> as a name.</summary>
    internal static bool Mentions(string? sql, string name)
    {
        if (sql is null)
        {
            return false;
        }

        try
        {
            return SqlTokenizer.Tokenize(sql).Any(t => (t.IsName || t.Kind == SqlTokenKind.String) && t.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (SqlParseException)
        {
            return true;
        }
    }
}

/// <summary>Script as CREATE / INSERT / DROP, and plans to rename or drop schema objects.</summary>
public static class SchemaObjectCommands
{
    /// <summary>The object's CREATE statement. For a table, its indexes and triggers follow.</summary>
    public static string ScriptCreate(SchemaObject obj, IReadOnlyList<SchemaObject> allObjects)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(allObjects);
        var parts = new List<string> { (obj.Sql ?? $"-- {obj.Name} is created by SQLite automatically.").TrimEnd(';') + ";" };
        if (obj.Type == SchemaObjectType.Table)
        {
            parts.AddRange(allObjects
                .Where(o => o.Type is SchemaObjectType.Index or SchemaObjectType.Trigger && o.Sql is not null && o.TableName.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Type)
                .Select(o => o.Sql!.TrimEnd(';') + ";"));
        }

        return string.Join("\n\n", parts) + "\n";
    }

    /// <summary>An INSERT template with NULL placeholders. Generated columns are left out.</summary>
    public static string ScriptInsert(TableDetails table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = table.Columns.Where(c => !c.IsGenerated).ToList();
        var names = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name)));
        var values = string.Join(",\n    ", columns.Select(c => $"NULL /* {c.Name.Replace("*/", "* /", StringComparison.Ordinal)} {(string.IsNullOrEmpty(c.DeclaredType) ? "ANY" : c.DeclaredType)} */"));
        return $"INSERT INTO {SqlIdentifier.Quote(table.Name)} ({names})\nVALUES (\n    {values}\n);\n";
    }

    public static string ScriptDrop(SchemaObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return $"DROP {TypeKeyword(obj.Type)} IF EXISTS {SqlIdentifier.Quote(obj.Name)};\n";
    }

    public static AlterPlan PlanDrop(SchemaObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return Transaction(AlterKind.Script, [TableSqlGenerator.Drop(TypeKeyword(obj.Type), obj.Name)]);
    }

    /// <summary>
    /// Tables are renamed with ALTER TABLE. Views, indexes, and triggers have no rename in SQLite,
    /// so they are dropped and created again under the new name in one transaction.
    /// </summary>
    public static AlterPlan PlanRename(SchemaObject obj, string newName)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (obj.Type == SchemaObjectType.Table)
        {
            return Transaction(AlterKind.Script, [$"ALTER TABLE {SqlIdentifier.Quote(obj.Name)} RENAME TO {SqlIdentifier.Quote(newName)}"]);
        }

        if (obj.Sql is null)
        {
            throw new InvalidOperationException($"\"{obj.Name}\" is created by SQLite automatically and cannot be renamed.");
        }

        return Transaction(AlterKind.Script, [TableSqlGenerator.Drop(TypeKeyword(obj.Type), obj.Name), RenameInCreateStatement(obj.Sql, newName)]);
    }

    /// <summary>Plan that replaces a view or trigger: drop the old one (if any), then create the new SQL.</summary>
    public static AlterPlan PlanReplace(SchemaObjectType type, string? oldName, string createSql)
    {
        ArgumentNullException.ThrowIfNull(createSql);
        var statements = new List<string>();
        if (oldName is not null)
        {
            statements.Add(TableSqlGenerator.Drop(TypeKeyword(type), oldName));
        }

        statements.Add(createSql);
        return Transaction(AlterKind.Script, statements);
    }

    /// <summary>Replaces the object name in a CREATE VIEW / INDEX / TRIGGER / TABLE statement.</summary>
    public static string RenameInCreateStatement(string createSql, string newName)
    {
        var c = new TokenCursor(createSql);
        c.Expect("CREATE");
        if (!c.Accept("TEMP"))
        {
            c.Accept("TEMPORARY");
        }

        c.Accept("UNIQUE");
        c.Accept("VIRTUAL");
        if (!(c.Accept("VIEW") || c.Accept("INDEX") || c.Accept("TRIGGER") || c.Accept("TABLE")))
        {
            throw c.Error("Expected VIEW, INDEX, TRIGGER, or TABLE");
        }

        if (c.Accept("IF"))
        {
            c.Expect("NOT");
            c.Expect("EXISTS");
        }

        var start = c.Peek().Start;
        c.ReadQualifiedName();
        var end = c.Tokens[c.Position - 1].End;
        return createSql[..start] + SqlIdentifier.Quote(newName) + createSql[end..];
    }

    public static string TypeKeyword(SchemaObjectType type) => type switch
    {
        SchemaObjectType.Table => "TABLE",
        SchemaObjectType.View => "VIEW",
        SchemaObjectType.Index => "INDEX",
        _ => "TRIGGER",
    };

    private static AlterPlan Transaction(AlterKind kind, IEnumerable<string> statements)
    {
        var steps = new List<PlanStep> { new(PlanStepKind.Begin, "BEGIN") };
        steps.AddRange(statements.Select(s => new PlanStep(PlanStepKind.Sql, s)));
        steps.Add(new PlanStep(PlanStepKind.Commit, "COMMIT"));
        return new AlterPlan(kind, steps, [], []);
    }
}
