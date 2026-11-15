using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqliteManager.App.Ui;
using SqliteManager.Core.Logging;

namespace SqliteManager.App.Panes;

/// <summary>Shows every SQL statement the application issued.</summary>
internal sealed partial class SqlLogView : UserControl
{
    private const int MaxItems = SqlLog.DefaultCapacity;

    private readonly ISqlLog _log;
    private readonly ListView _list;

    public SqlLogView(ISqlLog log)
    {
        _log = log;
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Time", LogicalToDeviceUnits(90));
        _list.Columns.Add("Database", LogicalToDeviceUnits(120));
        _list.Columns.Add("ms", LogicalToDeviceUnits(60), HorizontalAlignment.Right);
        _list.Columns.Add("SQL", LogicalToDeviceUnits(700));
        _list.Columns.Add("Error", LogicalToDeviceUnits(300));

        var copy = new ToolStripMenuItem("Copy SQL", null, (_, _) => CopySelected());
        var clear = new ToolStripMenuItem("Clear Log", null, (_, _) => _log.Clear());
        var menu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        menu.Items.AddRange([copy, clear]);
        menu.Opening += (_, _) => copy.Enabled = _list.SelectedItems.Count > 0;
        _list.ContextMenuStrip = menu;
        _list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelected();
                e.Handled = true;
            }
        };

        Controls.Add(_list);

        log.EntryAdded += OnEntryAdded;
        log.Cleared += OnCleared;
    }

    private void OnEntryAdded(object? sender, SqlLogEntry entry) => RunOnUi(() =>
    {
        _list.BeginUpdate();
        try
        {
            var item = _list.Items.Add(CreateItem(entry));
            while (_list.Items.Count > MaxItems)
            {
                _list.Items.RemoveAt(0);
            }

            item.EnsureVisible();
        }
        finally
        {
            _list.EndUpdate();
        }
    });

    private void OnCleared(object? sender, EventArgs e) => RunOnUi(_list.Items.Clear);

    private static ListViewItem CreateItem(SqlLogEntry entry)
    {
        var sql = Whitespace().Replace(entry.Sql, " ").Trim();
        if (entry.Parameters.Count > 0)
        {
            sql += "   -- " + string.Join(", ", entry.Parameters.Select(p => $"{p.Key} = {FormatParameter(p.Value)}"));
        }

        var item = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture))
        {
            Tag = entry,
            ToolTipText = entry.Error ?? entry.Sql,
        };
        item.SubItems.Add(entry.Database);
        item.SubItems.Add(entry.Duration.TotalMilliseconds.ToString("0.0", CultureInfo.CurrentCulture));
        item.SubItems.Add(sql);
        item.SubItems.Add(entry.Error ?? string.Empty);
        if (!entry.Succeeded)
        {
            item.ForeColor = Theme.ErrorText;
        }

        return item;
    }

    private static string FormatParameter(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        byte[] b => $"<BLOB {b.Length} bytes>",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private void CopySelected()
    {
        if (_list.SelectedItems.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (ListViewItem item in _list.SelectedItems)
        {
            var entry = (SqlLogEntry)item.Tag!;
            text.AppendLine(entry.Sql.TrimEnd().TrimEnd(';') + ";");
        }

        Clipboard.SetText(text.ToString());
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }
        else
        {
            action();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Entries added before the handle existed were skipped by RunOnUi; reload them.
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _log.GetEntries())
        {
            _list.Items.Add(CreateItem(entry));
        }

        _list.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.EntryAdded -= OnEntryAdded;
            _log.Cleared -= OnCleared;
        }

        base.Dispose(disposing);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
