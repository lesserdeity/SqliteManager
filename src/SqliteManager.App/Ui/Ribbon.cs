using System.ComponentModel;

namespace SqliteManager.App.Ui;

/// <summary>A tab on the ribbon, such as Home or View.</summary>
internal sealed class RibbonTab(string text)
{
    public string Text { get; } = text;

    public List<RibbonGroup> Groups { get; } = [];

    public RibbonGroup AddGroup(string text)
    {
        var group = new RibbonGroup(text);
        Groups.Add(group);
        return group;
    }
}

/// <summary>A labeled group of buttons on a ribbon tab.</summary>
internal sealed class RibbonGroup(string text)
{
    public string Text { get; } = text;

    public List<ToolStripItem> Items { get; } = [];
}

/// <summary>
/// An Office-style ribbon: a colored tab row with a File menu button, and a body that shows
/// the groups of the selected tab.
/// </summary>
internal sealed class Ribbon : Control
{
    private readonly List<RibbonTab> _tabs = [];
    private readonly List<Control> _tabBodies = [];
    private readonly Panel _body;
    private readonly List<Rectangle> _tabRects = [];
    private Rectangle _fileRect;
    private int _selectedIndex = -1;
    private int _hoverIndex = -2; // -1 = File button, -2 = none.

    public Ribbon()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 9f);
        _body = new Panel { BackColor = Theme.RibbonBody };
        Controls.Add(_body);
        Dock = DockStyle.Top;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? FileMenu { get; set; }

    private int HeaderHeight => LogicalToDeviceUnits(28);

    private int BodyHeight => LogicalToDeviceUnits(96);

    public void AddTab(RibbonTab tab)
    {
        _tabs.Add(tab);
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Theme.RibbonBody,
            Padding = new Padding(LogicalToDeviceUnits(4), LogicalToDeviceUnits(2), 0, 0),
            Visible = false,
        };

        foreach (var group in tab.Groups)
        {
            flow.Controls.Add(new RibbonGroupPanel(group, BodyHeight - LogicalToDeviceUnits(4)));
        }

        _tabBodies.Add(flow);
        _body.Controls.Add(flow);
        if (_selectedIndex < 0)
        {
            SelectTab(0);
        }

        PerformLayout();
        Invalidate();
    }

    public void SelectTab(RibbonTab tab) => SelectTab(_tabs.IndexOf(tab));

    public void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == _selectedIndex)
        {
            return;
        }

        if (_selectedIndex >= 0)
        {
            _tabBodies[_selectedIndex].Visible = false;
        }

        _selectedIndex = index;
        _tabBodies[index].Visible = true;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Height = HeaderHeight + BodyHeight + 1;
        _body.SetBounds(0, HeaderHeight, Width, BodyHeight);
        LayoutHeader();
    }

    private void LayoutHeader()
    {
        _tabRects.Clear();
        var pad = LogicalToDeviceUnits(14);
        var x = 0;
        var fileWidth = TextRenderer.MeasureText("File", Font).Width + pad * 2 + LogicalToDeviceUnits(6);
        _fileRect = new Rectangle(x, 0, fileWidth, HeaderHeight);
        x += fileWidth + LogicalToDeviceUnits(4);
        foreach (var tab in _tabs)
        {
            var w = TextRenderer.MeasureText(tab.Text, Font).Width + pad * 2;
            _tabRects.Add(new Rectangle(x, LogicalToDeviceUnits(3), w, HeaderHeight - LogicalToDeviceUnits(3)));
            x += w;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var header = new SolidBrush(Theme.Accent))
        {
            g.FillRectangle(header, 0, 0, Width, HeaderHeight);
        }

        using (var fileBrush = new SolidBrush(_hoverIndex == -1 ? Theme.AccentHover : Theme.AccentDark))
        {
            g.FillRectangle(fileBrush, _fileRect);
        }

        TextRenderer.DrawText(g, "File", Font, _fileRect, Theme.AccentText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        for (var i = 0; i < _tabs.Count; i++)
        {
            var rect = _tabRects[i];
            Color textColor;
            if (i == _selectedIndex)
            {
                using var brush = new SolidBrush(Theme.RibbonBody);
                g.FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height + 1);
                textColor = Theme.AccentOnBody;
            }
            else
            {
                if (i == _hoverIndex)
                {
                    using var hover = new SolidBrush(Theme.AccentHover);
                    g.FillRectangle(hover, rect);
                }

                textColor = Theme.AccentText;
            }

            TextRenderer.DrawText(g, _tabs[i].Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        using var border = new Pen(Theme.RibbonBorder);
        g.DrawLine(border, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitTest(e.Location);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -2;
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = HitTest(e.Location);
        if (hit == -1)
        {
            FileMenu?.Show(this, new Point(_fileRect.Left, _fileRect.Bottom));
        }
        else if (hit >= 0)
        {
            SelectTab(hit);
        }
    }

    private int HitTest(Point p)
    {
        if (_fileRect.Contains(p))
        {
            return -1;
        }

        for (var i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Contains(p))
            {
                return i;
            }
        }

        return -2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>One group: a row of large buttons with the group name underneath.</summary>
    private sealed class RibbonGroupPanel : Panel
    {
        private readonly Label _label;

        public RibbonGroupPanel(RibbonGroup group, int height)
        {
            BackColor = Theme.RibbonBody;
            Margin = new Padding(0);
            Padding = new Padding(LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(6), 0);

            var strip = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                CanOverflow = false,
                AutoSize = true,
                BackColor = Theme.RibbonBody,
                Renderer = new RibbonRenderer(),
                Padding = new Padding(0),
                ImageScalingSize = new Size(LogicalToDeviceUnits(32), LogicalToDeviceUnits(32)),
            };
            strip.Items.AddRange(group.Items.ToArray());

            _label = new Label
            {
                Text = group.Text,
                Dock = DockStyle.Bottom,
                Height = LogicalToDeviceUnits(18),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.RibbonGroupText,
                AutoSize = false,
            };

            Controls.Add(strip);
            Controls.Add(_label);

            var width = Math.Max(strip.PreferredSize.Width, TextRenderer.MeasureText(group.Text, _label.Font).Width + LogicalToDeviceUnits(12));
            Size = new Size(width + Padding.Horizontal, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.RibbonSeparator);
            var x = Width - 1;
            e.Graphics.DrawLine(pen, x, LogicalToDeviceUnits(4), x, Height - LogicalToDeviceUnits(4));
        }
    }
}

/// <summary>Flat Office-style rendering for ribbon and menu tool strips.</summary>
internal sealed class RibbonRenderer : ToolStripProfessionalRenderer
{
    public RibbonRenderer()
        : base(new RibbonColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBackground(e);
            return;
        }

        using var brush = new SolidBrush(e.BackColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton button)
        {
            base.OnRenderButtonBackground(e);
            return;
        }

        var rect = new Rectangle(Point.Empty, e.Item.Size);
        rect.Inflate(-1, -1);
        Color? fill = button.Pressed ? Theme.ButtonPressed
            : button.Selected && button.Enabled ? Theme.ButtonHover
            : button.Checked ? Theme.ButtonChecked
            : null;

        if (fill is { } color)
        {
            using var brush = new SolidBrush(color);
            using var pen = new Pen(Theme.ButtonHoverBorder);
            e.Graphics.FillRectangle(brush, rect);
            e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
    }

    private sealed class RibbonColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.ButtonHover;

        public override Color MenuItemBorder => Theme.ButtonHoverBorder;

        public override Color MenuItemSelectedGradientBegin => Theme.ButtonHover;

        public override Color MenuItemSelectedGradientEnd => Theme.ButtonHover;

        public override Color MenuItemPressedGradientBegin => Theme.ButtonPressed;

        public override Color MenuItemPressedGradientEnd => Theme.ButtonPressed;

        public override Color ToolStripDropDownBackground => Theme.WindowBack;

        public override Color ImageMarginGradientBegin => Theme.WindowBack;

        public override Color ImageMarginGradientMiddle => Theme.WindowBack;

        public override Color ImageMarginGradientEnd => Theme.WindowBack;

        public override Color MenuBorder => Theme.RibbonBorder;

        public override Color StatusStripGradientBegin => Theme.Accent;

        public override Color StatusStripGradientEnd => Theme.Accent;
    }
}

/// <summary>Helpers to build large ribbon buttons.</summary>
internal static class RibbonButtons
{
    public static ToolStripButton Large(string text, char glyph, EventHandler onClick, bool checkOnClick = false)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            ImageScaling = ToolStripItemImageScaling.SizeToFit,
            Image = Icons.Glyph(glyph, 64, Theme.GlyphColor, text[..1]),
            Padding = new Padding(4, 2, 4, 2),
            AutoSize = true,
            CheckOnClick = checkOnClick,
            ToolTipText = text.Replace('\n', ' '),
        };
        button.Click += onClick;
        return button;
    }
}
