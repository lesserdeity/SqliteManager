using System.ComponentModel;
using System.Globalization;
using SqliteManager.Core.Data;

namespace SqliteManager.App.Ui;

/// <summary>How a cell should look because of pending edits.</summary>
[Flags]
internal enum CellState
{
    None = 0,
    Changed = 1,
    RowModified = 2,
    RowInserted = 4,
    RowDeleted = 8,
}

/// <summary>
/// Double-buffered DataGridView with the application's styling, row numbers, value formatting
/// (NULL and BLOB shown in gray italics), and optional pending-edit styling.
/// </summary>
internal sealed class SqlDataGrid : DataGridView
{
    /// <summary>Cell value used while a row is still loading.</summary>
    public static readonly object Pending = new();

    /// <summary>Cell value of a new row's column that will use the table default.</summary>
    public static readonly object DefaultValue = new();

    private static Color ChangedBack => Theme.ChangedBack;
    private static Color InsertedBack => Theme.InsertedBack;
    private static Color DeletedBack => Theme.DeletedBack;

    private readonly Font _italic;
    private readonly Font _strike;
    private readonly Font _italicStrike;
    private readonly Font _markerFont;

    public SqlDataGrid()
    {
        DoubleBuffered = true;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AllowUserToOrderColumns = false;
        BorderStyle = BorderStyle.None;
        BackgroundColor = Theme.WindowBack;
        SelectionMode = DataGridViewSelectionMode.CellSelect;
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        ColumnHeadersHeight = LogicalToDeviceUnits(26);
        EnableHeadersVisualStyles = false;
        GridColor = Theme.GridLines;
        ColumnHeadersDefaultCellStyle.BackColor = Theme.PaneHeader;
        ColumnHeadersDefaultCellStyle.ForeColor = Theme.PaneHeaderText;
        ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.PaneHeader;
        RowHeadersDefaultCellStyle.BackColor = Theme.PaneHeader;
        RowHeadersDefaultCellStyle.SelectionBackColor = Theme.ButtonPressed;
        DefaultCellStyle.SelectionBackColor = Theme.ButtonPressed;
        DefaultCellStyle.SelectionForeColor = Theme.SelectionText;
        RowTemplate.Height = LogicalToDeviceUnits(22);
        _italic = new Font(Font, FontStyle.Italic);
        _strike = new Font(Font, FontStyle.Strikeout);
        _italicStrike = new Font(Font, FontStyle.Italic | FontStyle.Strikeout);
        _markerFont = new Font("Segoe UI Symbol", Font.Size);
    }

    /// <summary>Returns the pending-edit state of a cell (column -1 for the row header).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, int, CellState>? CellStateProvider { get; set; }

    /// <summary>Text shown for a value. NULL and BLOB are marked special so they look different from text.</summary>
    public static string FormatValue(object? value, out bool isSpecial)
    {
        isSpecial = value is null or byte[];
        return value switch
        {
            null => "NULL",
            byte[] blob => $"<BLOB {blob.Length:N0} bytes>",
            _ => ValueParser.FormatForEdit(value),
        };
    }

    /// <summary>Adds text columns sized to fit the header and the sample rows.</summary>
    public void SetColumns(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> sampleRows)
    {
        Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var width = TextRenderer.MeasureText(column.Name, ColumnHeadersDefaultCellStyle.Font ?? Font).Width + LogicalToDeviceUnits(12);
            foreach (var row in sampleRows.Take(50))
            {
                width = Math.Max(width, TextRenderer.MeasureText(FormatValue(row[i], out _), Font).Width);
            }

            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"c{i}",
                HeaderText = column.Name,
                ToolTipText = string.IsNullOrEmpty(column.DataTypeName) ? column.Name : $"{column.Name} ({column.DataTypeName})",
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ValueType = typeof(object),
                Width = Math.Clamp(width + LogicalToDeviceUnits(16), LogicalToDeviceUnits(50), LogicalToDeviceUnits(320)),
            });
        }
    }

    /// <summary>Sets the row count and widens the row header to fit the largest row number and an edit marker.</summary>
    public void SetRowCount(int rowCount)
    {
        RowHeadersWidth = TextRenderer.MeasureText(Math.Max(rowCount, 1).ToString("N0", CultureInfo.CurrentCulture), Font).Width
            + LogicalToDeviceUnits(CellStateProvider is null ? 20 : 34);
        RowCount = rowCount;
    }

    protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
    {
        base.OnCellFormatting(e);
        if (e.FormattingApplied || e.CellStyle is null)
        {
            return;
        }

        var state = e.RowIndex >= 0 && e.ColumnIndex >= 0 ? CellStateProvider?.Invoke(e.RowIndex, e.ColumnIndex) ?? CellState.None : CellState.None;
        var deleted = state.HasFlag(CellState.RowDeleted);
        if (state.HasFlag(CellState.Changed))
        {
            e.CellStyle.BackColor = ChangedBack;
        }
        else if (state.HasFlag(CellState.RowInserted))
        {
            e.CellStyle.BackColor = InsertedBack;
        }
        else if (deleted)
        {
            e.CellStyle.BackColor = DeletedBack;
        }

        bool special;
        if (ReferenceEquals(e.Value, Pending))
        {
            e.Value = "…";
            special = true;
        }
        else if (ReferenceEquals(e.Value, DefaultValue))
        {
            e.Value = "DEFAULT";
            special = true;
        }
        else
        {
            e.Value = FormatValue(e.Value, out special);
        }

        if (special || deleted)
        {
            e.CellStyle.ForeColor = Theme.NullText;
        }

        e.CellStyle.Font = (special, deleted) switch
        {
            (true, true) => _italicStrike,
            (true, false) => _italic,
            (false, true) => _strike,
            _ => e.CellStyle.Font,
        };
        e.FormattingApplied = true;
    }

    protected override void OnRowPostPaint(DataGridViewRowPostPaintEventArgs e)
    {
        base.OnRowPostPaint(e);
        var text = (e.RowIndex + 1).ToString("N0", CultureInfo.CurrentCulture);
        var bounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, RowHeadersWidth - LogicalToDeviceUnits(4), e.RowBounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, Theme.RibbonGroupText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var state = CellStateProvider?.Invoke(e.RowIndex, -1) ?? CellState.None;
        var (marker, color) = state switch
        {
            _ when state.HasFlag(CellState.RowDeleted) => ("✕", Theme.ErrorText),
            _ when state.HasFlag(CellState.RowInserted) => ("★", Theme.SuccessText),
            _ when state.HasFlag(CellState.RowModified) => ("✎", Theme.WarningText),
            _ => (null, Color.Empty),
        };
        if (marker is not null)
        {
            var markerBounds = new Rectangle(e.RowBounds.Left + LogicalToDeviceUnits(2), e.RowBounds.Top, LogicalToDeviceUnits(16), e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, marker, _markerFont, markerBounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _italic.Dispose();
            _strike.Dispose();
            _italicStrike.Dispose();
            _markerFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
