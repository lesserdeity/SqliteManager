using System.Globalization;
using SqliteManager.App.Ui;
using SqliteManager.Core.Maintenance;

namespace SqliteManager.App.Tools;

/// <summary>PRAGMA editor: journal mode, foreign keys, synchronous, cache size, page size, user version.</summary>
internal sealed class PragmaDialog : Form
{
    private readonly PragmaValues _current;
    private readonly ComboBox _journal;
    private readonly CheckBox _foreignKeys;
    private readonly ComboBox _synchronous;
    private readonly NumericUpDown _cacheSize;
    private readonly ComboBox _pageSize;
    private readonly NumericUpDown _userVersion;
    private readonly TextBox _preview;
    private readonly Button _ok;

    public PragmaDialog(string databaseName, PragmaValues current, bool readOnly)
    {
        _current = current;
        Text = $"PRAGMA Settings — {databaseName}";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _journal = Combo(PragmaService.JournalModes, current.JournalMode);
        _foreignKeys = new CheckBox { Text = "Enforce foreign keys", Checked = current.ForeignKeys, AutoSize = true };
        _synchronous = Combo(PragmaService.SynchronousModes, PragmaService.SynchronousModes[Math.Clamp(current.Synchronous, 0, 3)]);
        _cacheSize = new NumericUpDown { Minimum = int.MinValue, Maximum = int.MaxValue, Value = current.CacheSize, Width = 120, ThousandsSeparator = true };
        _pageSize = Combo(PragmaService.PageSizes.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList(), current.PageSize.ToString(CultureInfo.InvariantCulture));
        _userVersion = new NumericUpDown { Minimum = int.MinValue, Maximum = int.MaxValue, Value = current.UserVersion, Width = 120 };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "journal_mode", _journal, "WAL lets readers and a writer work at the same time. Saved in the file.");
        AddRow(grid, "foreign_keys", _foreignKeys, "For this connection only (not saved in the file).");
        AddRow(grid, "synchronous", _synchronous, "FULL is safest; NORMAL is faster (safe with WAL).");
        AddRow(grid, "cache_size", _cacheSize, "Pages, or KiB when negative. For this connection only.");
        AddRow(grid, "page_size", _pageSize, "Changing it runs VACUUM. Not possible in WAL mode.");
        AddRow(grid, "user_version", _userVersion, "A number for your own use, e.g. a schema version.");

        var info = new Label
        {
            AutoSize = true,
            ForeColor = Theme.MutedText,
            Text = $"Pages: {current.PageCount:N0}   Free pages: {current.FreelistCount:N0}   Size: {current.PageCount * current.PageSize / 1024.0:N0} KiB   Encoding: {current.Encoding}",
            Margin = new Padding(0, 0, 0, 8),
        };

        _preview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Width = 560,
            Height = 90,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            Margin = new Padding(0, 0, 0, 8),
        };

        _ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        AcceptButton = _ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
        buttons.Controls.AddRange([cancel, _ok]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(grid);
        layout.Controls.Add(info);
        if (readOnly)
        {
            layout.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.ErrorText, Text = "The database is open read-only: only foreign_keys and cache_size can change.", Margin = new Padding(0, 0, 0, 8) });
            _journal.Enabled = _synchronous.Enabled = _pageSize.Enabled = _userVersion.Enabled = false;
        }

        layout.Controls.Add(new Label { AutoSize = true, Text = "SQL to run:" });
        layout.Controls.Add(_preview);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        foreach (var control in new Control[] { _journal, _synchronous, _pageSize })
        {
            ((ComboBox)control).SelectedIndexChanged += (_, _) => UpdatePreview();
        }

        _foreignKeys.CheckedChanged += (_, _) => UpdatePreview();
        _cacheSize.ValueChanged += (_, _) => UpdatePreview();
        _userVersion.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public PragmaValues Wanted => _current with
    {
        JournalMode = (string)_journal.SelectedItem!,
        ForeignKeys = _foreignKeys.Checked,
        Synchronous = _synchronous.SelectedIndex,
        CacheSize = (long)_cacheSize.Value,
        PageSize = int.Parse((string)_pageSize.SelectedItem!, CultureInfo.InvariantCulture),
        UserVersion = (int)_userVersion.Value,
    };

    private void UpdatePreview()
    {
        try
        {
            var statements = PragmaService.PlanChanges(_current, Wanted);
            _preview.ForeColor = Theme.WindowText;
            _preview.Text = statements.Count == 0 ? "-- No changes." : string.Join(";\r\n", statements) + ";";
            _ok.Enabled = statements.Count > 0;
        }
        catch (ArgumentException ex)
        {
            _preview.ForeColor = Theme.ErrorText;
            _preview.Text = ex.Message;
            _ok.Enabled = false;
        }
    }

    private static ComboBox Combo(IReadOnlyList<string> items, string selected)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.SelectedItem = items.FirstOrDefault(i => i.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        return combo;
    }

    private static void AddRow(TableLayoutPanel grid, string name, Control editor, string hint)
    {
        grid.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Consolas", 9f), Margin = new Padding(0, 6, 12, 0) });
        editor.Margin = new Padding(0, 3, 12, 3);
        grid.Controls.Add(editor);
        grid.Controls.Add(new Label { Text = hint, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.MutedText, Margin = new Padding(0, 6, 0, 0) });
    }
}
