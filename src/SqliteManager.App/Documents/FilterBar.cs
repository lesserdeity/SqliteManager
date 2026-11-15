using SqliteManager.App.Ui;
using SqliteManager.Core.Data;

namespace SqliteManager.App.Documents;

/// <summary>A row of filter boxes above a grid, kept lined up with the grid's columns.</summary>
internal sealed class FilterBar : Panel
{
    private const string HelpText =
        "Filter this column:\n" +
        "  text           contains text\n" +
        "  =x  <>x        equal / not equal\n" +
        "  >x  >=x <x <=x compare (numbers compare as numbers)\n" +
        "  IS NULL        empty (NULL) values\n" +
        "  IS NOT NULL    values that are not NULL";

    private readonly DataGridView _grid;
    private readonly List<TextBox> _boxes = [];
    private readonly Label _label;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 15000 };
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 500 };
    private bool _suppress;

    public FilterBar(DataGridView grid)
    {
        _grid = grid;
        Dock = DockStyle.Top;
        BackColor = Theme.PaneHeader;
        Height = LogicalToDeviceUnits(26);

        _label = new Label
        {
            Text = "Filter",
            ForeColor = Theme.RibbonGroupText,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
        };
        Controls.Add(_label);
        _toolTip.SetToolTip(_label, HelpText);

        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            FiltersChanged?.Invoke(this, EventArgs.Empty);
        };

        _grid.ColumnWidthChanged += (_, _) => LayoutBoxes();
        _grid.ColumnDisplayIndexChanged += (_, _) => LayoutBoxes();
        _grid.RowHeadersWidthChanged += (_, _) => LayoutBoxes();
        _grid.Scroll += (_, e) =>
        {
            if (e.ScrollOrientation == ScrollOrientation.HorizontalScroll)
            {
                LayoutBoxes();
            }
        };
        _grid.Resize += (_, _) => LayoutBoxes();
    }

    /// <summary>Raised shortly after the user stops typing, or at once when Enter is pressed.</summary>
    public event EventHandler? FiltersChanged;

    public bool HasFilters => _boxes.Any(b => !string.IsNullOrWhiteSpace(b.Text));

    /// <summary>Creates one box per grid column. Text typed earlier is kept for columns with the same name.</summary>
    public void Rebuild()
    {
        var previous = _boxes.ToDictionary(b => (string)b.Tag!, b => b.Text, StringComparer.OrdinalIgnoreCase);
        foreach (var box in _boxes)
        {
            Controls.Remove(box);
            box.Dispose();
        }

        _boxes.Clear();
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            var name = column.Tag as string ?? column.HeaderText;
            var box = new TextBox
            {
                Tag = name,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "Filter",
                Text = previous.GetValueOrDefault(name, string.Empty),
            };
            box.TextChanged += (_, _) =>
            {
                if (_suppress)
                {
                    return;
                }

                _delay.Stop();
                _delay.Start();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    _delay.Stop();
                    FiltersChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (e.KeyCode == Keys.Escape && box.TextLength > 0)
                {
                    e.SuppressKeyPress = true;
                    box.Clear();
                }
            };
            _toolTip.SetToolTip(box, HelpText);
            _boxes.Add(box);
            Controls.Add(box);
        }

        LayoutBoxes();
    }

    public IReadOnlyList<ColumnFilter> GetFilters() =>
        _boxes.Where(b => !string.IsNullOrWhiteSpace(b.Text)).Select(b => new ColumnFilter((string)b.Tag!, b.Text)).ToList();

    /// <summary>Clears every box. Returns true if any box had text.</summary>
    public bool Clear()
    {
        var had = HasFilters;
        _delay.Stop();
        _suppress = true;
        try
        {
            foreach (var box in _boxes)
            {
                box.Clear();
            }
        }
        finally
        {
            _suppress = false;
        }

        return had;
    }

    private void LayoutBoxes()
    {
        var left = _grid.RowHeadersVisible ? _grid.RowHeadersWidth : 0;
        var boxHeight = Height - LogicalToDeviceUnits(4);
        _label.SetBounds(0, 0, left, Height);

        var x = left - _grid.HorizontalScrollingOffset;
        var columns = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex);
        foreach (var column in columns)
        {
            if (column.Index >= _boxes.Count)
            {
                continue;
            }

            var box = _boxes[column.Index];
            var boxLeft = Math.Max(x, left);
            var right = x + column.Width;
            box.Visible = right - boxLeft > LogicalToDeviceUnits(12);
            if (box.Visible)
            {
                box.SetBounds(boxLeft + 1, LogicalToDeviceUnits(2), right - boxLeft - 2, boxHeight);
            }

            x = right;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _delay.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
