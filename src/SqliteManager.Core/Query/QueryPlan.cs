using System.Globalization;
using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Query;

/// <summary>One step of an EXPLAIN QUERY PLAN result.</summary>
public sealed class QueryPlanNode(int id, int parent, string detail)
{
    public int Id { get; } = id;

    public int Parent { get; } = parent;

    public string Detail { get; } = detail;

    public List<QueryPlanNode> Children { get; } = [];

    /// <summary>True for a full table scan (SCAN without an index), which is often worth a look.</summary>
    public bool IsFullScan =>
        Detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase)
        && !Detail.Contains(" INDEX ", StringComparison.OrdinalIgnoreCase)
        && !Detail.Contains("CONSTANT ROW", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Runs EXPLAIN QUERY PLAN and turns the rows (id, parent, notused, detail) into a tree.</summary>
public static class QueryPlan
{
    /// <summary>Builds the tree. Rows whose parent is 0 or unknown become roots. Row order is kept.</summary>
    public static IReadOnlyList<QueryPlanNode> BuildTree(IEnumerable<(int Id, int Parent, string Detail)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byId = new Dictionary<int, QueryPlanNode>();
        var roots = new List<QueryPlanNode>();
        foreach (var (id, parent, detail) in rows)
        {
            var node = new QueryPlanNode(id, parent, detail);
            if (parent != 0 && byId.TryGetValue(parent, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }

            byId[id] = node;
        }

        return roots;
    }

    /// <summary>
    /// Explains the first statement of <paramref name="sql"/>. Parameters (@x, :x, $x) are bound to NULL so
    /// the plan can be shown without values.
    /// </summary>
    public static async Task<(SqlStatement Statement, IReadOnlyList<QueryPlanNode> Plan)> ExplainAsync(
        DatabaseSession session, string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sql);
        var statements = SqlScriptSplitter.Split(sql);
        if (statements.Count == 0)
        {
            throw new ArgumentException("There is no statement to explain.", nameof(sql));
        }

        var statement = statements[0];
        var text = statement.Text.Trim().TrimEnd(';');
        if (text.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The statement already starts with EXPLAIN.", nameof(sql));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var token in SqlTokenizer.Tokenize(text).Where(t => t.Kind == SqlTokenKind.Variable && t.Text.Length > 1 && t.Text[0] != '?'))
        {
            parameters[token.Text] = null;
        }

        var rows = await session.QueryAsync(
            "EXPLAIN QUERY PLAN " + text,
            r => (Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), r.GetString(3)),
            parameters.Count == 0 ? null : parameters,
            cancellationToken).ConfigureAwait(false);
        return (statement, BuildTree(rows));
    }
}
