using System.Globalization;
using SqliteManager.App.Ui;
using SqliteManager.Core.Query;

namespace SqliteManager.App.Panes;

/// <summary>Searchable list of past query runs.</summary>
internal sealed class HistoryView : UserControl
{
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly System.Windows.Forms.Timer _searchDelay;
    private List<QueryHistoryEntry> _entries = [];

    public HistoryView()
    {
        _search = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search history (SQL or database)",
            BorderStyle = BorderStyle.FixedSingle,
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Time", LogicalToDeviceUnits(130));
        _list.Columns.Add("Database", LogicalToDeviceUnits(120));
        _list.Columns.Add("ms", LogicalToDeviceUnits(60), HorizontalAlignment.Right);
        _list.Columns.Add("Rows", LogicalToDeviceUnits(70), HorizontalAlignment.Right);
        _list.Columns.Add("Status", LogicalToDeviceUnits(70));
        _list.Columns.Add("SQL", LogicalToDeviceUnits(800));
        _list.DoubleClick += (_, _) => RaiseOpen();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                RaiseOpen();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelected();
                e.Handled = true;
            }
        };

        var open = new ToolStripMenuItem("Open in New Query Tab", null, (_, _) => RaiseOpen());
        var copy = new ToolStripMenuItem("Copy SQL", null, (_, _) => CopySelected());
        var menu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        menu.Items.AddRange([open, copy]);
        menu.Opening += (_, e) => e.Cancel = Selected is null;
        _list.ContextMenuStrip = menu;

        _searchDelay = new System.Windows.Forms.Timer { Interval = 250 };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Populate();
        };
        _search.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };

        Controls.Add(_list);
        Controls.Add(_search);
    }

    /// <summary>Raised when the user wants to open an entry's SQL.</summary>
    public event EventHandler<QueryHistoryEntry>? OpenRequested;

    private QueryHistoryEntry? Selected => _list.SelectedItems.Count == 1 ? (QueryHistoryEntry)_list.SelectedItems[0].Tag! : null;

    /// <summary>Shows <paramref name="entries"/> (newest first).</summary>
    public void SetEntries(IEnumerable<QueryHistoryEntry> entries)
    {
        _entries = entries.ToList();
        Populate();
    }

    /// <summary>Adds a new entry at the top.</summary>
    public void Add(QueryHistoryEntry entry, int maxEntries)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > maxEntries)
        {
            _entries.RemoveRange(maxEntries, _entries.Count - maxEntries);
        }

        Populate();
    }

    private void Populate()
    {
        var matches = QueryHistoryStore.Search(_entries, _search.Text);
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var entry in matches)
            {
                var sql = string.Join(" ", entry.Sql.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
                var item = new ListViewItem(entry.Timestamp.ToString("g", CultureInfo.CurrentCulture))
                {
                    Tag = entry,
                    ToolTipText = entry.Error is null ? entry.Sql : entry.Sql + Environment.NewLine + Environment.NewLine + entry.Error,
                };
                item.SubItems.Add(Path.GetFileName(entry.DatabasePath));
                item.SubItems.Add(entry.DurationMs.ToString("N0", CultureInfo.CurrentCulture));
                item.SubItems.Add(entry.Rows.ToString("N0", CultureInfo.CurrentCulture));
                item.SubItems.Add(entry.Succeeded ? "OK" : "Failed");
                item.SubItems.Add(sql.Length > 500 ? sql[..500] + "…" : sql);
                if (!entry.Succeeded)
                {
                    item.ForeColor = Theme.ErrorText;
                }

                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private void RaiseOpen()
    {
        if (Selected is { } entry)
        {
            OpenRequested?.Invoke(this, entry);
        }
    }

    private void CopySelected()
    {
        if (Selected is { } entry)
        {
            Clipboard.SetText(entry.Sql);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchDelay.Dispose();
        }

        base.Dispose(disposing);
    }
}
