namespace SqliteManager.App.Ui;

/// <summary>A TabControl that draws an X on the right of each tab header and raises <see cref="CloseRequested"/> when it is clicked.</summary>
internal sealed class ClosableTabControl : TabControl
{
    private int _hoverCloseIndex = -1;
    private int _pressedCloseIndex = -1;

    public ClosableTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;

        // Paint the whole header strip (not only the tabs) so it follows the theme.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // Tab width = text width + 2 * Padding.X; the extra room on the right holds the X.
        Padding = new Point(LogicalToDeviceUnits(18), LogicalToDeviceUnits(5));
        ShowToolTips = true;
    }

    /// <summary>Raised when the user clicks a tab's X.</summary>
    public event EventHandler<TabPage>? CloseRequested;

    private int CloseSize => LogicalToDeviceUnits(16);

    /// <summary>The X button area inside the header of tab <paramref name="index"/>.</summary>
    private Rectangle GetCloseRect(int index)
    {
        var tab = GetTabRect(index);
        var size = CloseSize;
        return new Rectangle(tab.Right - size - LogicalToDeviceUnits(5), tab.Top + (tab.Height - size) / 2, size, size);
    }

    private int HitTestClose(Point p)
    {
        for (var i = 0; i < TabCount; i++)
        {
            if (GetCloseRect(i).Contains(p))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Theme.WorkspaceBack))
        {
            g.FillRectangle(back, ClientRectangle);
        }

        for (var i = 0; i < TabCount; i++)
        {
            DrawTab(g, i);
        }

        // A line under the tabs, and the page border.
        var page = DisplayRectangle;
        using var border = new Pen(Theme.RibbonBorder);
        g.DrawRectangle(border, page.Left - 1, page.Top - 1, page.Width + 1, page.Height + 1);
    }

    private void DrawTab(Graphics g, int index)
    {
        var tab = GetTabRect(index);
        var selected = index == SelectedIndex;

        using (var back = new SolidBrush(selected ? Theme.WindowBack : Theme.PaneHeader))
        {
            g.FillRectangle(back, tab);
        }

        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, tab.Left, tab.Top, tab.Width, LogicalToDeviceUnits(2));
        }

        var close = GetCloseRect(index);
        var textRect = Rectangle.FromLTRB(tab.Left + LogicalToDeviceUnits(6), tab.Top, close.Left - LogicalToDeviceUnits(2), tab.Bottom);
        TextRenderer.DrawText(g, TabPages[index].Text, Font, textRect, selected ? Theme.PaneHeaderText : Theme.RibbonGroupText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var hover = index == _hoverCloseIndex;
        if (hover)
        {
            using var hoverBrush = new SolidBrush(index == _pressedCloseIndex ? Theme.ButtonPressed : Theme.ButtonHover);
            g.FillRectangle(hoverBrush, close);
        }

        // Draw the X; show it brighter on the selected or hovered tab.
        var inset = LogicalToDeviceUnits(4);
        using var pen = new Pen(hover ? Theme.ErrorText : selected ? Theme.PaneHeaderText : Theme.RibbonGroupText, LogicalToDeviceUnits(1) * 1.4f);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawLine(pen, close.Left + inset, close.Top + inset, close.Right - inset - 1, close.Bottom - inset - 1);
        g.DrawLine(pen, close.Right - inset - 1, close.Top + inset, close.Left + inset, close.Bottom - inset - 1);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitTestClose(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pressedCloseIndex = -1;
        SetHover(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressedCloseIndex = HitTestClose(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Close only when the press and release are both on the same X.
        var index = HitTestClose(e.Location);
        var pressed = _pressedCloseIndex;
        _pressedCloseIndex = -1;
        Invalidate();
        if (index >= 0 && index == pressed)
        {
            CloseRequested?.Invoke(this, TabPages[index]);
        }
    }

    private void SetHover(int index)
    {
        if (index != _hoverCloseIndex)
        {
            _hoverCloseIndex = index;
            Invalidate();
        }
    }
}
