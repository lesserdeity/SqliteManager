using System.Globalization;
using SqliteManager.App.Ui;
using SqliteManager.Core.QueryDesign;

namespace SqliteManager.App.QueryDesigner;

/// <summary>
/// The grid below the canvas: one row per field with Field, Table, Alias, Total, Show, Sort, Sort Order,
/// Criteria, and Or columns. The grid edits <see cref="DesignField"/>s in place.
/// </summary>
internal sealed class CriteriaGrid : DataGridView
{
    private const int MinOrColumns = 3;
    private const string NoTable = "";

    private static readonly (TotalKind Kind, string Text)[] Totals =
    [
        (TotalKind.GroupBy, "Group By"), (TotalKind.Count, "Count"), (TotalKind.Sum, "Sum"), (TotalKind.Avg, "Avg"),
        (TotalKind.Min, "Min"), (TotalKind.Max, "Max"), (TotalKind.Expression, "Expression"), (TotalKind.Where, "Where"),
    ];

    private static readonly (SortDirection Sort, string Text)[] Sorts =
        [(SortDirection.None, ""), (SortDirection.Ascending, "Ascending"), (SortDirection.Descending, "Descending")];

    private readonly DataGridViewTextBoxColumn _field;
    private readonly DataGridViewComboBoxColumn _table;
    private readonly DataGridViewTextBoxColumn _alias;
    private readonly DataGridViewComboBoxColumn _total;
    private readonly DataGridViewCheckBoxColumn _show;
    private readonly DataGridViewComboBoxColumn _sort;
    private readonly DataGridViewTextBoxColumn _sortOrder;
    private readonly DataGridViewTextBoxColumn _criteria;
    private readonly List<DataGridViewTextBoxColumn> _or = [];
    private QueryDesignDocument _design = new();
    private bool _loading;

    public CriteriaGrid()
    {
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        BackgroundColor = Theme.WindowBack;
        GridColor = Theme.GridLines;
        EnableHeadersVisualStyles = false;
        ColumnHeadersDefaultCellStyle.BackColor = Theme.PaneHeader;
        ColumnHeadersDefaultCellStyle.ForeColor = Theme.PaneHeaderText;
        ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.PaneHeader;
        RowHeadersDefaultCellStyle.BackColor = Theme.PaneHeader;
        RowHeadersDefaultCellStyle.SelectionBackColor = Theme.ButtonPressed;
        DefaultCellStyle.BackColor = Theme.WindowBack;
        DefaultCellStyle.ForeColor = Theme.WindowText;
        DefaultCellStyle.SelectionBackColor = Theme.ButtonPressed;
        DefaultCellStyle.SelectionForeColor = Theme.SelectionText;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        ColumnHeadersHeight = LogicalToDeviceUnits(26);
        RowHeadersWidth = LogicalToDeviceUnits(28);
        RowTemplate.Height = LogicalToDeviceUnits(22);
        AllowUserToAddRows = true;
        AllowUserToDeleteRows = true;
        AllowUserToResizeRows = false;
        SelectionMode = DataGridViewSelectionMode.CellSelect;
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _field = TextColumn("Field", 170, "A column of the table, * for all columns, or an SQL expression (with no table)");
        _table = ComboColumn("Table", 120, "The table of the column. Empty: the Field is an expression");
        _alias = TextColumn("Alias", 100, "Output column name (AS)");
        _total = ComboColumn("Total", 95, "Group by or aggregate (Totals mode)");
        _total.Items.AddRange(Totals.Select(t => (object)t.Text).ToArray());
        _show = new DataGridViewCheckBoxColumn { HeaderText = "Show", Width = LogicalToDeviceUnits(44), ToolTipText = "Include the field in the result", SortMode = DataGridViewColumnSortMode.NotSortable };
        _sort = ComboColumn("Sort", 90, "Sort the result by this field");
        _sort.Items.AddRange(Sorts.Select(s => (object)s.Text).ToArray());
        _sortOrder = TextColumn("Sort Order", 70, "Position in ORDER BY when several fields are sorted (1 first)");
        _criteria = TextColumn("Criteria", 150, "abc, 5, >5, <>x, LIKE A%, IN (1, 2), BETWEEN 1 AND 9, IS NULL ... Values in a row are combined with AND");
        Columns.AddRange([_field, _table, _alias, _total, _show, _sort, _sortOrder, _criteria]);
        SetOrColumnCount(MinOrColumns);

        CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (IsCurrentCellDirty && CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        CellValueChanged += (_, _) => ReadBack();
        CellClick += (_, e) =>
        {
            // One click opens a drop-down list (the grid otherwise needs a second click or F4).
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && Columns[e.ColumnIndex] is DataGridViewComboBoxColumn && BeginEdit(false)
                && EditingControl is ComboBox combo)
            {
                combo.DroppedDown = true;
            }
        };
        UserDeletedRow += (_, _) => ReadBack();
        DataError += (_, e) => e.ThrowException = false;
        DefaultValuesNeeded += (_, e) =>
        {
            e.Row.Cells[_show.Index].Value = true;
            e.Row.Cells[_table.Index].Value = NoTable;
            e.Row.Cells[_total.Index].Value = Totals[0].Text;
            e.Row.Cells[_sort.Index].Value = Sorts[0].Text;
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !IsCurrentCellInEditMode && SelectedCells.Count > 0 && CurrentCell?.OwningColumn is DataGridViewTextBoxColumn)
            {
                foreach (DataGridViewCell cell in SelectedCells)
                {
                    if (cell.OwningRow is { IsNewRow: false } && cell.OwningColumn is DataGridViewTextBoxColumn)
                    {
                        cell.Value = null;
                    }
                }

                e.Handled = true;
            }
        };
    }

    /// <summary>Raised after the user changed a field in the grid.</summary>
    public event EventHandler? FieldsChanged;

    public void SetDesign(QueryDesignDocument design)
    {
        _design = design;
        Reload();
    }

    /// <summary>Fills the grid from the design (after the canvas or a load changed it).</summary>
    public void Reload()
    {
        if (IsCurrentCellInEditMode)
        {
            CancelEdit();
            EndEdit();
        }

        _loading = true;
        try
        {
            _table.Items.Clear();
            _table.Items.Add(NoTable);
            foreach (var table in _design.Tables)
            {
                _table.Items.Add(table.Alias);
            }

            _total.Visible = _design.Totals;
            SetOrColumnCount(Math.Max(MinOrColumns, _design.Fields.Count == 0 ? 0 : _design.Fields.Max(f => f.Or.Count)));
            Rows.Clear();
            foreach (var field in _design.Fields)
            {
                var row = Rows[Rows.Add()];
                row.Cells[_field.Index].Value = field.Field;
                row.Cells[_table.Index].Value = _design.FindTable(field.Table)?.Alias ?? NoTable;
                row.Cells[_alias.Index].Value = field.Alias;
                row.Cells[_total.Index].Value = Totals.First(t => t.Kind == field.Total).Text;
                row.Cells[_show.Index].Value = field.Show;
                row.Cells[_sort.Index].Value = Sorts.First(s => s.Sort == field.Sort).Text;
                row.Cells[_sortOrder.Index].Value = field.SortOrder?.ToString(CultureInfo.InvariantCulture);
                row.Cells[_criteria.Index].Value = field.Criteria;
                for (var i = 0; i < _or.Count; i++)
                {
                    row.Cells[_or[i].Index].Value = i < field.Or.Count ? field.Or[i] : null;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Writes the grid back to the design's fields.</summary>
    private void ReadBack()
    {
        if (_loading)
        {
            return;
        }

        var fields = new List<DesignField>();
        foreach (DataGridViewRow row in Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var field = new DesignField
            {
                Field = (row.Cells[_field.Index].Value as string)?.Trim() ?? string.Empty,
                Table = row.Cells[_table.Index].Value is string { Length: > 0 } table ? table : null,
                Alias = Blank(row.Cells[_alias.Index].Value),
                Show = row.Cells[_show.Index].Value is true,
                Total = Totals.FirstOrDefault(t => t.Text == row.Cells[_total.Index].Value as string).Kind,
                Sort = Sorts.FirstOrDefault(s => s.Text == row.Cells[_sort.Index].Value as string).Sort,
                SortOrder = int.TryParse(row.Cells[_sortOrder.Index].Value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : null,
                Criteria = Blank(row.Cells[_criteria.Index].Value),
                Or = _or.Select(c => Blank(row.Cells[c.Index].Value)).ToList(),
            };

            // Drop empty trailing "or" rows so the file stays small.
            while (field.Or.Count > 0 && field.Or[^1] is null)
            {
                field.Or.RemoveAt(field.Or.Count - 1);
            }

            fields.Add(field);
        }

        _design.Fields.Clear();
        _design.Fields.AddRange(fields);
        FieldsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? Blank(object? value) => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private void SetOrColumnCount(int count)
    {
        while (_or.Count < count)
        {
            var column = TextColumn(_or.Count == 0 ? "Or" : $"Or {_or.Count + 1}", 120, "Another criteria row: combined with the rows above using OR");
            _or.Add(column);
            Columns.Add(column);
        }
    }

    private DataGridViewTextBoxColumn TextColumn(string header, int width, string tip) => new()
    {
        HeaderText = header,
        Width = LogicalToDeviceUnits(width),
        ToolTipText = tip,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private DataGridViewComboBoxColumn ComboColumn(string header, int width, string tip) => new()
    {
        HeaderText = header,
        Width = LogicalToDeviceUnits(width),
        ToolTipText = tip,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        FlatStyle = FlatStyle.Flat,
    };
}
