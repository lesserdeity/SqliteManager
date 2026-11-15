using System.Drawing.Drawing2D;
using SqliteManager.App.Ui;
using SqliteManager.Core.QueryDesign;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.QueryDesigner;

/// <summary>What changed on the canvas.</summary>
internal enum CanvasChange
{
    /// <summary>A table box moved. Only positions changed.</summary>
    Layout,

    /// <summary>Tables, joins, or fields changed.</summary>
    Design,
}

/// <summary>Payload of a table dragged from the object explorer.</summary>
internal static class ExplorerDrag
{
    public const string Format = "SqliteManager.SchemaObject";

    public static string Encode(Guid session, string name) => session.ToString("N") + "\n" + name;

    public static (Guid Session, string Name)? Decode(IDataObject? data)
    {
        if (data?.GetDataPresent(Format) != true || data.GetData(Format) is not string text)
        {
            return null;
        }

        var parts = text.Split('\n', 2);
        return parts.Length == 2 && Guid.TryParse(parts[0], out var id) ? (id, parts[1]) : null;
    }
}

/// <summary>
/// Access-style design surface, drawn with GDI+: one box per table with a check box per column,
/// join lines between columns. Drag a header to move a box, drag a column onto another to join,
/// click a join line to change it, and drop tables from the object explorer.
/// </summary>
internal sealed class DesignCanvas : Panel
{
    private const int BoxWidth = 190;
    private const int HeaderHeight = 24;
    private const int RowHeight = 19;
    private const int MaxVisibleRows = 14;

    private readonly ContextMenuStrip _joinMenu;
    private readonly ContextMenuStrip _tableMenu;
    private readonly Font _headerFont;
    private readonly Font _keyFont;
    private readonly Font _rowFont;
    private readonly Dictionary<string, int> _zOrder = new(StringComparer.OrdinalIgnoreCase);
    private int _zCounter;

    private QueryDesignDocument _design = new();
    private DesignTable? _moving;
    private Point _moveOffset;
    private (DesignTable Table, string Column, Point Start)? _joinDrag;
    private Point _joinDragPoint;
    private bool _joinDragActive;
    private DesignJoin? _menuJoin;
    private DesignTable? _menuTable;
    private readonly Dictionary<string, int> _scroll = new(StringComparer.OrdinalIgnoreCase);

    public DesignCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        AllowDrop = true;
        BackColor = Theme.CanvasBack;
        // Fonts in 96-dpi pixels: the canvas draws in design units and scales with a transform.
        _headerFont = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Pixel);
        _keyFont = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        _rowFont = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);

        _joinMenu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        _joinMenu.Opening += (_, _) => FillJoinMenu();
        _tableMenu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        _tableMenu.Items.Add("Remove Table", null, (_, _) =>
        {
            if (_menuTable is not null)
            {
                RemoveTable(_menuTable);
            }
        });
        _tableMenu.Items.Add("Add All Columns", null, (_, _) =>
        {
            if (_menuTable is not null)
            {
                foreach (var column in ColumnsOf(_menuTable))
                {
                    if (!_design.Fields.Any(f => f.IsColumnOf(_menuTable.Alias, column.Name)))
                    {
                        _design.Fields.Add(new DesignField { Table = _menuTable.Alias, Field = column.Name });
                    }
                }

                OnChanged(CanvasChange.Design);
            }
        });
    }

    /// <summary>Raised after the user changed the design on the canvas.</summary>
    public event EventHandler<CanvasChange>? Changed;

    /// <summary>Raised when a table is dropped from the object explorer (database id, name, design position).</summary>
    public event EventHandler<(Guid Session, string Name, Point Location)>? TableDropped;

    /// <summary>Columns of a table by table name, or null if the table is not in the database.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, IReadOnlyList<ColumnInfo>?> ColumnsProvider { get; set; } = _ => null;

    /// <summary>Foreign-key columns by table name (shown in bold).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, IReadOnlySet<string>> ForeignKeyProvider { get; set; } = _ => new HashSet<string>();

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsReadOnly { get; set; }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public QueryDesignDocument Design
    {
        get => _design;
        set
        {
            _design = value;
            _scroll.Clear();
            UpdateScrollSize();
            Invalidate();
        }
    }

    /// <summary>A free spot for a new box, right of the existing ones.</summary>
    public Point NextFreeLocation()
    {
        if (_design.Tables.Count == 0)
        {
            return new Point(20, 20);
        }

        var right = _design.Tables.Max(t => t.X + BoxWidth);
        return new Point(right + 60, 20);
    }

    /// <summary>Converts a screen point to design coordinates (96-dpi pixels, unscrolled).</summary>
    public Point ScreenToDesign(Point screen)
    {
        var client = PointToClient(screen);
        return new Point(Unscale(client.X - AutoScrollPosition.X), Unscale(client.Y - AutoScrollPosition.Y));
    }

    public void RefreshDesign()
    {
        UpdateScrollSize();
        Invalidate();
    }

    // ---- Geometry (design coordinates, unscaled) -------------------------------------------

    private float ScaleFactor => DeviceDpi / 96f;

    private int Scale(int value) => (int)Math.Round(value * ScaleFactor);

    private int Unscale(int value) => (int)Math.Round(value / ScaleFactor);

    private IReadOnlyList<ColumnInfo> ColumnsOf(DesignTable table) => ColumnsProvider(table.Table) ?? [];

    /// <summary>Rows in a box: "*" and the columns.</summary>
    private int RowCountOf(DesignTable table) => ColumnsOf(table).Count + 1;

    private int VisibleRows(DesignTable table) => Math.Min(RowCountOf(table), MaxVisibleRows);

    private int FirstRow(DesignTable table) =>
        Math.Clamp(_scroll.GetValueOrDefault(table.Alias), 0, Math.Max(0, RowCountOf(table) - VisibleRows(table)));

    private static Rectangle BoxBounds(DesignTable table, int visibleRows) =>
        new(table.X, table.Y, BoxWidth, HeaderHeight + (visibleRows * RowHeight) + 4);

    private Rectangle BoxBounds(DesignTable table) => BoxBounds(table, VisibleRows(table));

    private static Rectangle CloseBounds(Rectangle box) => new(box.Right - 20, box.Top + 4, 16, 16);

    /// <summary>Middle Y of a row (0 = "*"), clamped to the visible part of the box.</summary>
    private int RowMiddle(DesignTable table, int row)
    {
        var box = BoxBounds(table);
        var visible = row - FirstRow(table);
        if (visible < 0)
        {
            return box.Top + HeaderHeight;
        }

        if (visible >= VisibleRows(table))
        {
            return box.Bottom - 2;
        }

        return box.Top + HeaderHeight + (visible * RowHeight) + (RowHeight / 2);
    }

    private int RowOfColumn(DesignTable table, string column)
    {
        var columns = ColumnsOf(table);
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, column, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return -1;
    }

    private enum Part
    {
        None,
        Header,
        Close,
        CheckBox,
        Column,
    }

    /// <summary>The box (topmost first) and the part of it at a design point. Row 0 is "*".</summary>
    private (DesignTable? Table, Part Part, int Row) HitTest(Point p)
    {
        foreach (var table in ZOrdered().Reverse())
        {
            var box = BoxBounds(table);
            if (!box.Contains(p))
            {
                continue;
            }

            if (p.Y < box.Top + HeaderHeight)
            {
                return (table, CloseBounds(box).Contains(p) && !IsReadOnly ? Part.Close : Part.Header, -1);
            }

            var row = FirstRow(table) + ((p.Y - box.Top - HeaderHeight) / RowHeight);
            if (row >= RowCountOf(table))
            {
                return (table, Part.None, -1);
            }

            return (table, p.X < box.Left + 22 ? Part.CheckBox : Part.Column, row);
        }

        return (null, Part.None, -1);
    }

    /// <summary>The polyline of a join line: out of one box, across, into the other.</summary>
    private Point[]? JoinPath(DesignJoin join)
    {
        var a = _design.FindTable(join.LeftTable);
        var b = _design.FindTable(join.RightTable);
        if (a is null || b is null)
        {
            return null;
        }

        var boxA = BoxBounds(a);
        var boxB = BoxBounds(b);
        var rowA = RowOfColumn(a, join.LeftColumn);
        var rowB = RowOfColumn(b, join.RightColumn);
        var ya = rowA < 0 ? boxA.Top + (HeaderHeight / 2) : RowMiddle(a, rowA);
        var yb = rowB < 0 ? boxB.Top + (HeaderHeight / 2) : RowMiddle(b, rowB);
        const int stub = 14;

        if (ReferenceEquals(a, b))
        {
            return [new(boxA.Right, ya), new(boxA.Right + stub, ya), new(boxA.Right + stub, yb), new(boxB.Right, yb)];
        }

        if (boxA.Right + (2 * stub) <= boxB.Left)
        {
            return [new(boxA.Right, ya), new(boxA.Right + stub, ya), new(boxB.Left - stub, yb), new(boxB.Left, yb)];
        }

        if (boxB.Right + (2 * stub) <= boxA.Left)
        {
            return [new(boxA.Left, ya), new(boxA.Left - stub, ya), new(boxB.Right + stub, yb), new(boxB.Right, yb)];
        }

        // Overlapping horizontally: go out on the right of both.
        var x = Math.Max(boxA.Right, boxB.Right) + stub;
        return [new(boxA.Right, ya), new(x, ya), new(x, yb), new(boxB.Right, yb)];
    }

    private DesignJoin? JoinAt(Point p)
    {
        const double tolerance = 5;
        foreach (var join in _design.Joins)
        {
            var path = JoinPath(join);
            if (path is null)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 1; i++)
            {
                if (DistanceToSegment(p, path[i], path[i + 1]) <= tolerance)
                {
                    return join;
                }
            }
        }

        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        double x = a.X + (t * dx) - p.X, y = a.Y + (t * dy) - p.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private void UpdateScrollSize()
    {
        var right = 0;
        var bottom = 0;
        foreach (var table in _design.Tables)
        {
            var box = BoxBounds(table);
            right = Math.Max(right, box.Right + 60);
            bottom = Math.Max(bottom, box.Bottom + 40);
        }

        AutoScrollMinSize = new Size(Scale(right), Scale(bottom));
    }

    // ---- Painting ------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        g.ScaleTransform(ScaleFactor, ScaleFactor);

        if (_design.Tables.Count == 0)
        {
            g.ResetTransform();
            TextRenderer.DrawText(g, "Drag tables here from the Object Explorer, or use Add Table.", Font, ClientRectangle, Theme.MutedText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        foreach (var join in _design.Joins)
        {
            DrawJoin(g, join);
        }

        foreach (var table in ZOrdered())
        {
            DrawTable(g, table);
        }

        if (_joinDrag is { } drag && _joinDragActive)
        {
            using var pen = new Pen(Theme.Accent, 1.5f) { DashStyle = DashStyle.Dash };
            var row = RowOfColumn(drag.Table, drag.Column);
            var box = BoxBounds(drag.Table);
            var start = new Point(_joinDragPoint.X >= box.Left + (box.Width / 2) ? box.Right : box.Left, RowMiddle(drag.Table, row));
            g.DrawLine(pen, start, _joinDragPoint);
        }
    }

    private void DrawJoin(Graphics g, DesignJoin join)
    {
        var path = JoinPath(join);
        if (path is null)
        {
            return;
        }

        using var pen = new Pen(join.Kind == JoinKind.Cross ? Theme.MutedText : Theme.JoinLine, 1.6f);
        if (join.Kind == JoinKind.Cross)
        {
            pen.DashStyle = DashStyle.Dash;
        }

        if (join.Kind == JoinKind.Left)
        {
            // The arrow points to the table whose rows may be missing (as in Access).
            pen.CustomEndCap = new AdjustableArrowCap(4, 5);
        }

        g.DrawLines(pen, path);
    }

    private void DrawTable(Graphics g, DesignTable table)
    {
        var box = BoxBounds(table);
        var columns = ColumnsProvider(table.Table);
        var missing = columns is null;
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        {
            g.FillRectangle(shadow, box.X + 3, box.Y + 3, box.Width, box.Height);
        }

        using (var back = new SolidBrush(Theme.BoxBack))
        {
            g.FillRectangle(back, box);
        }

        var header = new Rectangle(box.X, box.Y, box.Width, HeaderHeight);
        using (var headerBrush = new SolidBrush(missing ? Theme.ErrorText : Theme.Accent))
        {
            g.FillRectangle(headerBrush, header);
        }

        var title = string.Equals(table.Alias, table.Table, StringComparison.Ordinal) ? table.Table : $"{table.Alias} ({table.Table})";
        if (missing)
        {
            title += " — not found";
        }

        var titleRect = new Rectangle(header.X + 6, header.Y, header.Width - 28, header.Height);
        DrawText(g, title, _headerFont, titleRect, Theme.AccentText);
        if (!IsReadOnly)
        {
            var close = CloseBounds(box);
            using var pen = new Pen(Theme.AccentText, 1.4f);
            g.DrawLine(pen, close.Left + 4, close.Top + 4, close.Right - 4, close.Bottom - 4);
            g.DrawLine(pen, close.Right - 4, close.Top + 4, close.Left + 4, close.Bottom - 4);
        }

        var keys = ForeignKeyProvider(table.Table);
        var first = FirstRow(table);
        var visible = VisibleRows(table);
        var all = columns ?? [];
        for (var i = 0; i < visible; i++)
        {
            var row = first + i;
            var y = box.Top + HeaderHeight + (i * RowHeight);
            var name = row == 0 ? "*" : all[row - 1].Name;
            var isKey = row > 0 && (all[row - 1].IsPrimaryKey || keys.Contains(name));
            var check = new Rectangle(box.Left + 6, y + 3, 12, 12);
            var isChecked = _design.Fields.Any(f => f.IsColumnOf(table.Alias, name));
            DrawCheckBox(g, check, isChecked);
            var textRect = new Rectangle(box.Left + 24, y, box.Width - 30, RowHeight);
            var text = row > 0 && all[row - 1].IsPrimaryKey ? name + "  (PK)" : name;
            DrawText(g, text, isKey ? _keyFont : _rowFont, textRect, Theme.BoxText);
        }

        if (RowCountOf(table) > visible)
        {
            // A small scroll indicator: the box shows part of the columns (use the mouse wheel).
            var trackHeight = box.Height - HeaderHeight - 4;
            var thumb = Math.Max(10, trackHeight * visible / RowCountOf(table));
            var top = box.Top + HeaderHeight + ((trackHeight - thumb) * first / Math.Max(1, RowCountOf(table) - visible));
            using var brush = new SolidBrush(Theme.MutedText);
            g.FillRectangle(brush, box.Right - 5, top, 3, thumb);
        }

        using var border = new Pen(Theme.RibbonBorder);
        g.DrawRectangle(border, box);
    }

    private static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None,
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawCheckBox(Graphics g, Rectangle bounds, bool isChecked)
    {
        using var back = new SolidBrush(isChecked ? Theme.Accent : Theme.BoxBack);
        using var border = new Pen(isChecked ? Theme.Accent : Theme.MutedText);
        g.FillRectangle(back, bounds);
        g.DrawRectangle(border, bounds);
        if (isChecked)
        {
            using var tick = new Pen(Theme.AccentText, 1.6f);
            g.DrawLines(tick, [new Point(bounds.Left + 3, bounds.Top + 6), new Point(bounds.Left + 5, bounds.Top + 9), new Point(bounds.Right - 2, bounds.Top + 3)]);
        }
    }

    // ---- Mouse ---------------------------------------------------------------------------------

    private Point ToDesign(Point client) =>
        new(Unscale(client.X - AutoScrollPosition.X), Unscale(client.Y - AutoScrollPosition.Y));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var p = ToDesign(e.Location);
        var (table, part, row) = HitTest(p);
        if (e.Button == MouseButtons.Right)
        {
            if (table is not null && !IsReadOnly)
            {
                _menuTable = table;
                _tableMenu.Show(this, e.Location);
            }
            else if (JoinAt(p) is { } join && !IsReadOnly)
            {
                _menuJoin = join;
                _joinMenu.Show(this, e.Location);
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (table is not null)
        {
            BringToFront(table);
        }

        switch (part)
        {
            case Part.Header:
                _moving = table;
                _moveOffset = new Point(p.X - table!.X, p.Y - table.Y);
                Capture = true;
                break;
            case Part.Close:
                RemoveTable(table!);
                break;
            case Part.CheckBox when !IsReadOnly:
                ToggleField(table!, row);
                break;
            case Part.Column when row > 0 && !IsReadOnly:
                _joinDrag = (table!, ColumnsOf(table!)[row - 1].Name, p);
                _joinDragActive = false;
                Capture = true;
                break;
            case Part.None when table is null && JoinAt(p) is { } join && !IsReadOnly:
                _menuJoin = join;
                _joinMenu.Show(this, e.Location);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToDesign(e.Location);
        if (_moving is not null)
        {
            _moving.X = Math.Max(0, p.X - _moveOffset.X);
            _moving.Y = Math.Max(0, p.Y - _moveOffset.Y);
            UpdateScrollSize();
            Invalidate();
            return;
        }

        if (_joinDrag is { } drag)
        {
            if (!_joinDragActive && (Math.Abs(p.X - drag.Start.X) > 4 || Math.Abs(p.Y - drag.Start.Y) > 4))
            {
                _joinDragActive = true;
            }

            if (_joinDragActive)
            {
                _joinDragPoint = p;
                var target = HitTest(p);
                Cursor = target is { Table: not null, Row: > 0 } && target.Part is Part.Column or Part.CheckBox && !ReferenceEquals(target.Table, drag.Table)
                    ? Cursors.Cross
                    : Cursors.No;
                Invalidate();
            }

            return;
        }

        var hit = HitTest(p);
        Cursor = hit.Part == Part.Header ? Cursors.SizeAll
            : hit.Part is Part.Close or Part.CheckBox ? Cursors.Hand
            : hit.Table is null && JoinAt(p) is not null ? Cursors.Hand
            : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        if (_moving is not null)
        {
            _moving = null;
            OnChanged(CanvasChange.Layout);
            return;
        }

        if (_joinDrag is { } drag)
        {
            _joinDrag = null;
            Cursor = Cursors.Default;
            if (_joinDragActive)
            {
                _joinDragActive = false;
                var target = HitTest(ToDesign(e.Location));
                if (target is { Table: not null, Row: > 0 } && target.Part is Part.Column or Part.CheckBox && !ReferenceEquals(target.Table, drag.Table))
                {
                    _design.AddJoin(drag.Table.Alias, drag.Column, target.Table.Alias, ColumnsOf(target.Table)[target.Row - 1].Name);
                    OnChanged(CanvasChange.Design);
                }

                Invalidate();
            }
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (table, part, row) = HitTest(ToDesign(e.Location));
        if (table is null || IsReadOnly || e.Button != MouseButtons.Left)
        {
            return;
        }

        if (part == Part.Column)
        {
            // Double-click adds the column as another field (as in Access), even if it is already used.
            _design.Fields.Add(new DesignField { Table = table.Alias, Field = row == 0 ? "*" : ColumnsOf(table)[row - 1].Name });
            OnChanged(CanvasChange.Design);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var (table, _, _) = HitTest(ToDesign(e.Location));
        if (table is not null && RowCountOf(table) > VisibleRows(table))
        {
            var first = FirstRow(table) - Math.Sign(e.Delta) * 3;
            _scroll[table.Alias] = Math.Clamp(first, 0, RowCountOf(table) - VisibleRows(table));
            Invalidate();
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    /// <summary>Tables in drawing order (last drawn is on top). The design order is not changed: it sets the FROM order.</summary>
    private IEnumerable<DesignTable> ZOrdered() => _design.Tables.OrderBy(t => _zOrder.GetValueOrDefault(t.Alias)).ToList();

    private void BringToFront(DesignTable table)
    {
        _zOrder[table.Alias] = ++_zCounter;
        Invalidate();
    }

    private void ToggleField(DesignTable table, int row)
    {
        var name = row == 0 ? "*" : ColumnsOf(table)[row - 1].Name;
        var removed = _design.Fields.RemoveAll(f => f.IsColumnOf(table.Alias, name));
        if (removed == 0)
        {
            _design.Fields.Add(new DesignField { Table = table.Alias, Field = name });
        }

        OnChanged(CanvasChange.Design);
    }

    private void RemoveTable(DesignTable table)
    {
        _design.RemoveTable(table.Alias);
        _scroll.Remove(table.Alias);
        _zOrder.Remove(table.Alias);
        OnChanged(CanvasChange.Design);
    }

    private void FillJoinMenu()
    {
        _joinMenu.Items.Clear();
        if (_menuJoin is not { } join)
        {
            return;
        }

        var left = join.LeftTable;
        var right = join.RightTable;
        var isLeft = join.Kind == JoinKind.Left;
        _joinMenu.Items.Add(new ToolStripLabel($"{left}.{join.LeftColumn} = {right}.{join.RightColumn}") { ForeColor = Theme.MutedText });
        _joinMenu.Items.Add(new ToolStripSeparator());
        AddKind("Inner join: only rows that match in both tables", join.Kind == JoinKind.Inner, () => _design.SetJoinKind(join, JoinKind.Inner));
        AddKind($"Left join: all rows of {left}, matching rows of {right}", isLeft, () => _design.SetJoinKind(join, JoinKind.Left, left));
        AddKind($"Left join: all rows of {right}, matching rows of {left}", false, () => _design.SetJoinKind(join, JoinKind.Left, right));
        AddKind("Cross join: every combination (ignores the columns)", join.Kind == JoinKind.Cross, () => _design.SetJoinKind(join, JoinKind.Cross));
        _joinMenu.Items.Add(new ToolStripSeparator());
        _joinMenu.Items.Add("Delete Join", null, (_, _) =>
        {
            _design.Joins.Remove(join);
            OnChanged(CanvasChange.Design);
        });

        void AddKind(string text, bool isChecked, Action apply) =>
            _joinMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) =>
            {
                apply();
                OnChanged(CanvasChange.Design);
            })
            { Checked = isChecked });
    }

    private void OnChanged(CanvasChange change)
    {
        UpdateScrollSize();
        Invalidate();
        Changed?.Invoke(this, change);
    }

    // ---- Drag and drop from the object explorer ------------------------------------------------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = !IsReadOnly && ExplorerDrag.Decode(e.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (!IsReadOnly && ExplorerDrag.Decode(e.Data) is { } payload)
        {
            TableDropped?.Invoke(this, (payload.Session, payload.Name, ScreenToDesign(new Point(e.X, e.Y))));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _joinMenu.Dispose();
            _tableMenu.Dispose();
            _headerFont.Dispose();
            _keyFont.Dispose();
            _rowFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
