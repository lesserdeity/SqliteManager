using SqliteManager.App.Ui;
using SqliteManager.Core.Data;

namespace SqliteManager.App.Documents;

/// <summary>Shows one query result set in a virtual-mode grid, with an Export button for read-only statements.</summary>
internal sealed class ResultGrid : UserControl
{
    private readonly IReadOnlyList<object?[]> _rows;

    /// <param name="onExport">Opens the export wizard for this statement, or null if it cannot be exported (not a read-only statement).</param>
    public ResultGrid(RowSet rows, bool truncated, int rowLimit, Action? onExport = null)
    {
        _rows = rows.Rows;
        var grid = new SqlDataGrid { Dock = DockStyle.Fill, VirtualMode = true };
        grid.CellValueNeeded += (_, e) => e.Value = _rows[e.RowIndex][e.ColumnIndex];
        grid.SetColumns(rows.Columns, rows.Rows);
        grid.SetRowCount(rows.Rows.Count);

        var text = $"{rows.Rows.Count:N0} rows";
        if (truncated)
        {
            text += $" (stopped at the row limit of {rowLimit:N0}; change it in File > Options, or export to get every row)";
        }

        var footer = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false, BackColor = Theme.RibbonBody, Renderer = new RibbonRenderer() };
        footer.Items.Add(new ToolStripStatusLabel(text) { ForeColor = truncated ? Theme.ErrorText : SystemColors.ControlText, Spring = true, TextAlign = ContentAlignment.MiddleLeft });
        if (onExport is not null)
        {
            footer.Items.Add(new ToolStripButton("Export...", null, (_, _) => onExport()) { ToolTipText = "Export all rows of this statement (it runs again)" });
        }

        Controls.Add(grid);
        Controls.Add(footer);
    }
}
