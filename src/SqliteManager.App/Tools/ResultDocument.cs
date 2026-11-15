using SqliteManager.App.Documents;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;

namespace SqliteManager.App.Tools;

/// <summary>A read-only tab that shows the rows of a check (integrity check, foreign key check) with a heading.</summary>
internal sealed class ResultDocument : UserControl
{
    public ResultDocument(string heading, RowSet rows, bool isProblem)
    {
        var label = new Label
        {
            Text = heading,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = LogicalToDeviceUnits(30),
            Padding = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.PaneHeader,
            ForeColor = isProblem ? Theme.ErrorText : Theme.PaneHeaderText,
            Font = new Font("Segoe UI Semibold", 9.5f),
        };
        Controls.Add(new ResultGrid(rows, truncated: false, rowLimit: rows.Rows.Count) { Dock = DockStyle.Fill });
        Controls.Add(label);
    }
}
