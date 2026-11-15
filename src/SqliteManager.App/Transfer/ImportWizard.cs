using System.Globalization;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Transfer;

namespace SqliteManager.App.Transfer;

/// <summary>Import wizard: source file and options with a preview, target table and load mode, column mapping, run, save as job.</summary>
internal sealed class ImportWizard : WizardForm
{
    private const string NoSource = "(none)";
    private static readonly string[] Cultures = ["(invariant: 1234.5)", "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL"];

    private readonly JobsService _jobs;
    private readonly ISqlLog _log;
    private readonly ImportJob _job;
    private readonly bool _editingJob;

    // Source page
    private readonly TextBox _file = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _delimiter = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 80 };
    private readonly TextBox _quote = new() { Width = 40, Text = "\"" };
    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox _hasHeader = new() { Text = "First row has column names", AutoSize = true, Checked = true };
    private readonly NumericUpDown _skipRows = new() { Width = 70, Maximum = 100_000 };
    private readonly ComboBox _sheet = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly TextBox _startCell = new() { Width = 70, Text = "A1" };
    private readonly TextBox _dateFormat = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional, e.g. dd/MM/yyyy;dd/MM/yyyy HH:mm (default: yyyy-MM-dd)" };
    private readonly ComboBox _culture = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 180 };
    private readonly DataGridView _preview = PreviewGrid();
    private readonly Label _previewMessage = new() { Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.RibbonGroupText };
    private readonly System.Windows.Forms.Timer _previewDelay = new() { Interval = 400 };

    // Target page
    private readonly ComboBox _database;
    private readonly Control _databasePicker;
    private readonly ComboBox _table = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly CheckBox _createTable = new() { Text = "Create the table if it does not exist (column types are guessed from the file)", AutoSize = true, Checked = true };
    private readonly ComboBox _loadMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly TextBox _keys = new() { Dock = DockStyle.Fill, PlaceholderText = "Key columns for upsert, e.g. rep_id" };
    private readonly ComboBox _errorMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly TextBox _preSql = new() { Multiline = true, Height = 54, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f) };
    private readonly TextBox _postSql = new() { Multiline = true, Height = 54, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f) };

    // Mapping page
    private readonly DataGridView _mapping = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        BackgroundColor = Theme.WindowBack,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter,
    };

    // Run page
    private readonly TextBox _summary = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.WindowBack, Font = new Font("Consolas", 9.5f) };
    private readonly Button _run = new() { Text = "Run Import", Width = 120, Height = 30 };
    private readonly Button _cancelRun = new() { Text = "Cancel", Width = 90, Height = 30, Enabled = false };
    private readonly Button _saveJob = new() { Text = "Save as Job...", Width = 120, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

    private IReadOnlyList<string> _sourceColumns = [];
    private List<(string Name, string Type)> _targetColumns = [];
    private CancellationTokenSource? _cts;
    private bool _loading;

    public ImportWizard(JobsService jobs, ISqlLog log, IEnumerable<DatabaseSession> openSessions, ImportJob? job = null, string? databasePath = null, string? table = null)
        : base(job is null ? "Import Data" : $"Edit Import Job: {job.Name}")
    {
        _jobs = jobs;
        _log = log;
        _editingJob = job is not null;
        _job = job ?? new ImportJob { DatabasePath = databasePath ?? string.Empty, TargetTable = table ?? string.Empty, CreateTableIfMissing = true };
        (_databasePicker, _database) = DatabasePicker(openSessions);

        _format.Items.AddRange(["CSV (comma separated)", "TSV (tab separated)", "Excel workbook (.xlsx)", "Excel 97-2003 (.xls)", "JSON (array of objects)"]);
        _delimiter.Items.AddRange([",", ";", "Tab", "|"]);
        _encoding.Items.AddRange(["utf-8", "utf-16", "windows-1252"]);
        _culture.Items.AddRange([.. Cultures]);
        _loadMode.Items.AddRange(["Append: add the rows", "Truncate: delete all rows first, then add", "Replace: drop the table and create it from the file", "Upsert: update rows with the same key, add the rest"]);
        _errorMode.Items.AddRange(["Roll back: stop at the first bad row, import nothing", "Skip: skip bad rows and list them in an error file"]);

        AddPage("Source", BuildSourcePage());
        AddPage("Target", BuildTargetPage());
        AddPage("Columns", BuildMappingPage());
        AddPage("Run", BuildRunPage());
        LoadFromJob();

        _previewDelay.Tick += async (_, _) =>
        {
            _previewDelay.Stop();
            await RefreshPreviewAsync();
        };
    }

    /// <summary>Raised after a successful import (with the database path).</summary>
    public event EventHandler<string>? Imported;

    private Control BuildSourcePage()
    {
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Right, Width = 90 };
        browse.Click += (_, _) => BrowseFile();
        var fileRow = new Panel { Height = 26, Dock = DockStyle.Fill };
        fileRow.Controls.Add(_file);
        fileRow.Controls.Add(browse);

        var csvRow = Row(new Label { Text = "Delimiter", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, _delimiter,
            new Label { Text = "Quote", AutoSize = true, Margin = new Padding(12, 7, 4, 0) }, _quote,
            new Label { Text = "Encoding", AutoSize = true, Margin = new Padding(12, 7, 4, 0) }, _encoding,
            new Label { Text = "Skip rows", AutoSize = true, Margin = new Padding(12, 7, 4, 0) }, _skipRows);
        var excelRow = Row(new Label { Text = "Sheet", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, _sheet,
            new Label { Text = "Start cell", AutoSize = true, Margin = new Padding(12, 7, 4, 0) }, _startCell);

        var form = Form(
            ("File:", fileRow),
            ("Format:", _format),
            ("Text files:", csvRow),
            ("Excel:", excelRow),
            (string.Empty, _hasHeader),
            ("Dates:", _dateFormat),
            ("Numbers:", _culture));

        foreach (var control in new Control[] { _file, _quote, _startCell, _dateFormat })
        {
            control.TextChanged += (_, _) => SchedulePreview();
        }

        foreach (var combo in new[] { _delimiter, _encoding, _sheet, _culture })
        {
            combo.TextChanged += (_, _) => SchedulePreview();
        }

        _skipRows.ValueChanged += (_, _) => SchedulePreview();
        _hasHeader.CheckedChanged += (_, _) => SchedulePreview();
        _format.SelectedIndexChanged += async (_, _) =>
        {
            UpdateSourceOptionsState();
            await LoadSheetsAsync();
            SchedulePreview();
        };

        var page = new Panel();
        var previewLabel = new Label { Text = "Preview (first 100 rows):", Dock = DockStyle.Top, Height = 22, Padding = new Padding(0, 4, 0, 0) };
        page.Controls.Add(_preview);
        page.Controls.Add(_previewMessage);
        page.Controls.Add(previewLabel);
        page.Controls.Add(form);
        return page;
    }

    private Control BuildTargetPage()
    {
        _database.TextChanged += async (_, _) =>
        {
            if (!_loading)
            {
                await LoadTablesAsync();
            }
        };
        _loadMode.SelectedIndexChanged += (_, _) => _keys.Enabled = _loadMode.SelectedIndex == 3;
        var page = new Panel();
        page.Controls.Add(Form(
            ("Database:", _databasePicker),
            ("Table:", _table),
            (string.Empty, _createTable),
            ("Load mode:", _loadMode),
            ("Key columns:", _keys),
            ("Bad rows:", _errorMode),
            ("SQL before:", _preSql),
            ("SQL after:", _postSql)));
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            ForeColor = Theme.RibbonGroupText,
            Text = "The SQL before and after runs in the same transaction as the rows. If anything fails, nothing is imported.",
        });
        return page;
    }

    private Control BuildMappingPage()
    {
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Source column", FlatStyle = FlatStyle.Flat, FillWeight = 40 });
        _mapping.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Target column", FillWeight = 35 });
        _mapping.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Default value (when no source)", FillWeight = 25 });
        _mapping.DataError += (_, e) => e.ThrowException = false;
        var page = new Panel();
        page.Controls.Add(_mapping);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Theme.RibbonGroupText,
            Text = "Choose the source column for each target column, or (none) to skip it. A target column with no source can get a default value. Clear a target name to leave it out.",
        });
        return page;
    }

    private Control BuildRunPage()
    {
        _run.Click += async (_, _) => await RunImportAsync();
        _cancelRun.Click += (_, _) => _cts?.Cancel();
        _saveJob.Click += async (_, _) => await SaveJobAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_run, _cancelRun, _saveJob, _status]);
        if (_editingJob)
        {
            _saveJob.Text = "Save Job";
        }

        var page = new Panel();
        page.Controls.Add(_summary);
        page.Controls.Add(buttons);
        return page;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    // ---- Job <-> controls ---------------------------------------------------------------

    private void LoadFromJob()
    {
        _loading = true;
        try
        {
            var o = _job.SourceOptions ?? new SourceOptions();
            _file.Text = _job.SourcePath;
            _format.SelectedIndex = (int)_job.SourceFormat;
            _delimiter.Text = o.Delimiter is "\t" ? "Tab" : o.Delimiter ?? (_job.SourceFormat == SourceFormat.Tsv ? "Tab" : ",");
            _quote.Text = o.Quote ?? "\"";
            _encoding.SelectedItem = _encoding.Items.Contains(o.Encoding ?? "utf-8") ? o.Encoding ?? "utf-8" : "utf-8";
            _hasHeader.Checked = o.HasHeader ?? true;
            _skipRows.Value = o.SkipRows ?? 0;
            _startCell.Text = o.StartCell ?? "A1";
            _dateFormat.Text = o.DateFormat ?? string.Empty;
            _culture.Text = string.IsNullOrEmpty(o.NumberCulture) ? Cultures[0] : o.NumberCulture;
            _database.Text = _job.DatabasePath;
            _table.Text = _job.TargetTable;
            _createTable.Checked = _job.CreateTableIfMissing;
            _loadMode.SelectedIndex = (int)_job.LoadMode;
            _keys.Text = string.Join(", ", _job.KeyColumns ?? []);
            _keys.Enabled = _job.LoadMode == LoadMode.Upsert;
            _errorMode.SelectedIndex = (int)_job.OnError;
            _preSql.Text = _job.PreSql ?? string.Empty;
            _postSql.Text = _job.PostSql ?? string.Empty;
            UpdateSourceOptionsState();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Copies the controls into the job (everything except the column mapping).</summary>
    private void SaveToJob()
    {
        _job.SourcePath = _file.Text.Trim();
        _job.SourceFormat = (SourceFormat)Math.Max(0, _format.SelectedIndex);
        _job.SourceOptions = CurrentSourceOptions();
        _job.DatabasePath = _database.Text.Trim();
        _job.TargetTable = _table.Text.Trim();
        _job.CreateTableIfMissing = _createTable.Checked;
        _job.LoadMode = (LoadMode)Math.Max(0, _loadMode.SelectedIndex);
        _job.KeyColumns = _job.LoadMode == LoadMode.Upsert
            ? _keys.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : null;
        _job.OnError = (ErrorMode)Math.Max(0, _errorMode.SelectedIndex);
        _job.PreSql = _preSql.Text;
        _job.PostSql = _postSql.Text;
    }

    private SourceOptions CurrentSourceOptions()
    {
        var format = (SourceFormat)Math.Max(0, _format.SelectedIndex);
        var isText = format is SourceFormat.Csv or SourceFormat.Tsv;
        var isExcel = format is SourceFormat.Xlsx or SourceFormat.Xls;
        return new SourceOptions
        {
            Delimiter = isText ? (_delimiter.Text == "Tab" ? "\t" : _delimiter.Text) : null,
            Quote = isText && _quote.Text.Length > 0 ? _quote.Text[..1] : null,
            Encoding = isText ? _encoding.SelectedItem as string : null,
            HasHeader = format == SourceFormat.Json ? null : _hasHeader.Checked,
            SkipRows = isText && _skipRows.Value > 0 ? (int)_skipRows.Value : null,
            SheetName = isExcel ? _sheet.SelectedItem as string : null,
            StartCell = isExcel && !string.IsNullOrWhiteSpace(_startCell.Text) && _startCell.Text.Trim() != "A1" ? _startCell.Text.Trim() : null,
            DateFormat = string.IsNullOrWhiteSpace(_dateFormat.Text) ? null : _dateFormat.Text.Trim(),
            NumberCulture = _culture.Text == Cultures[0] || string.IsNullOrWhiteSpace(_culture.Text) ? null : _culture.Text.Trim(),
        };
    }

    private void UpdateSourceOptionsState()
    {
        var format = (SourceFormat)Math.Max(0, _format.SelectedIndex);
        var isText = format is SourceFormat.Csv or SourceFormat.Tsv;
        var isExcel = format is SourceFormat.Xlsx or SourceFormat.Xls;
        foreach (var c in new Control[] { _delimiter, _quote, _encoding, _skipRows })
        {
            c.Enabled = isText;
        }

        _sheet.Enabled = _startCell.Enabled = isExcel;
        _hasHeader.Enabled = format != SourceFormat.Json;
    }

    // ---- Source page ----------------------------------------------------------------------

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Data files (*.csv;*.tsv;*.txt;*.xlsx;*.xls;*.json)|*.csv;*.tsv;*.txt;*.xlsx;*.xls;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _file.Text = dialog.FileName;
            _format.SelectedIndex = (int)SourceReaders.FormatFromExtension(dialog.FileName);
            if (_format.SelectedIndex == (int)SourceFormat.Tsv)
            {
                _delimiter.Text = "Tab";
            }

            if (string.IsNullOrWhiteSpace(_table.Text))
            {
                _table.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private async Task LoadSheetsAsync()
    {
        var format = (SourceFormat)Math.Max(0, _format.SelectedIndex);
        _sheet.Items.Clear();
        if (format is not (SourceFormat.Xlsx or SourceFormat.Xls) || !File.Exists(_file.Text))
        {
            return;
        }

        try
        {
            var path = _file.Text;
            var sheets = await Task.Run(() => SourceReaders.ListSheets(path, format));
            _sheet.Items.AddRange([.. sheets]);
            var wanted = _job.SourceOptions?.SheetName;
            _sheet.SelectedItem = sheets.FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? sheets.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _previewMessage.Text = "Cannot read the sheets: " + ex.Message;
        }
    }

    private void SchedulePreview()
    {
        if (!_loading)
        {
            _previewDelay.Stop();
            _previewDelay.Start();
        }
    }

    private async Task RefreshPreviewAsync()
    {
        _preview.Columns.Clear();
        var path = _file.Text.Trim();
        if (!File.Exists(path))
        {
            _previewMessage.Text = path.Length == 0 ? "Choose a file." : "The file does not exist.";
            _previewMessage.ForeColor = Theme.ErrorText;
            return;
        }

        try
        {
            var format = (SourceFormat)Math.Max(0, _format.SelectedIndex);
            var options = CurrentSourceOptions();
            var (columns, rows) = await Task.Run(() => SourceReaders.Preview(path, format, options));
            _sourceColumns = columns;
            foreach (var column in columns)
            {
                _preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, SortMode = DataGridViewColumnSortMode.NotSortable });
            }

            foreach (var row in rows)
            {
                _preview.Rows.Add(row.Values.Select(v => v switch
                {
                    null => string.Empty,
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString(),
                }).Cast<object>().ToArray());
            }

            var errors = rows.Count(r => r.Error is not null);
            _previewMessage.Text = $"{columns.Count} column(s); showing {rows.Count} row(s)." + (errors > 0 ? $" {errors} row(s) have a problem (see the row number)." : string.Empty);
            _previewMessage.ForeColor = errors > 0 ? Theme.ErrorText : Theme.RibbonGroupText;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _sourceColumns = [];
            _previewMessage.Text = "Cannot read the file: " + ex.Message;
            _previewMessage.ForeColor = Theme.ErrorText;
        }
    }

    // ---- Target and mapping pages -------------------------------------------------------

    private async Task LoadTablesAsync()
    {
        var path = _database.Text.Trim();
        var current = _table.Text;
        _table.Items.Clear();
        _table.Text = current;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var (manager, session) = await OpenAsync(path, _log, readOnly: true);
            await using (manager)
            {
                var tables = await session.QueryAsync("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0));
                _table.Items.AddRange([.. tables]);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Cannot read the database: " + ex.Message;
        }
    }

    private async Task LoadTargetColumnsAsync()
    {
        _targetColumns = [];
        var path = _database.Text.Trim();
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(_table.Text))
        {
            return;
        }

        var (manager, session) = await OpenAsync(path, _log, readOnly: true);
        await using (manager)
        {
            _targetColumns = await session.QueryAsync(
                "SELECT name, type FROM pragma_table_xinfo(@t) WHERE hidden NOT IN (1, 2, 3) ORDER BY cid",
                r => (r.GetString(0), r.GetString(1)),
                new Dictionary<string, object?> { ["@t"] = _table.Text.Trim() });
        }
    }

    private void FillMapping()
    {
        var sourceColumn = (DataGridViewComboBoxColumn)_mapping.Columns[0];
        sourceColumn.Items.Clear();
        sourceColumn.Items.Add(NoSource);
        sourceColumn.Items.AddRange([.. _sourceColumns]);
        _mapping.Rows.Clear();

        var saved = _job.ColumnMappings;
        var replace = (LoadMode)_loadMode.SelectedIndex == LoadMode.Replace;
        if (_targetColumns.Count > 0 && !replace)
        {
            foreach (var (name, _) in _targetColumns)
            {
                var mapping = saved?.FirstOrDefault(m => m.Target.Equals(name, StringComparison.OrdinalIgnoreCase));
                var source = mapping is not null
                    ? mapping.Source
                    : _sourceColumns.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
                _mapping.Rows.Add(source is not null && _sourceColumns.Contains(source) ? source : NoSource, name, mapping?.Default ?? string.Empty);
            }
        }
        else
        {
            foreach (var source in _sourceColumns)
            {
                var mapping = saved?.FirstOrDefault(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase));
                _mapping.Rows.Add(source, mapping?.Target ?? source, string.Empty);
            }
        }

        // The target names come from the table when it exists; only new tables can name their columns.
        _mapping.Columns[1].ReadOnly = _targetColumns.Count > 0 && !replace;
    }

    private List<ColumnMapping> ReadMapping()
    {
        _mapping.EndEdit();
        var result = new List<ColumnMapping>();
        foreach (DataGridViewRow row in _mapping.Rows)
        {
            var source = row.Cells[0].Value as string;
            var target = (row.Cells[1].Value as string)?.Trim();
            var fallback = row.Cells[2].Value as string;
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            if (source is null or NoSource)
            {
                if (!string.IsNullOrEmpty(fallback))
                {
                    result.Add(new ColumnMapping { Target = target, Default = fallback });
                }

                continue;
            }

            result.Add(new ColumnMapping { Source = source, Target = target });
        }

        return result;
    }

    // ---- Navigation ---------------------------------------------------------------------

    protected override async Task<bool> CanLeavePageAsync(int index)
    {
        switch (index)
        {
            case 0:
                await RefreshPreviewAsync();
                if (_sourceColumns.Count == 0)
                {
                    MessageBox.Show(this, "Choose a file that the preview can read.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                if (_database.Text.Length == 0 && _database.Items.Count > 0)
                {
                    _database.SelectedIndex = 0;
                }

                if (string.IsNullOrWhiteSpace(_table.Text))
                {
                    _table.Text = Path.GetFileNameWithoutExtension(_file.Text.Trim());
                }

                return true;
            case 1:
                if (!File.Exists(_database.Text.Trim()) || string.IsNullOrWhiteSpace(_table.Text))
                {
                    MessageBox.Show(this, "Choose an existing database file and a table name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                if (_loadMode.SelectedIndex == 3 && string.IsNullOrWhiteSpace(_keys.Text))
                {
                    MessageBox.Show(this, "Upsert needs the key columns that identify a row.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                await LoadTargetColumnsAsync();
                if (_targetColumns.Count == 0 && !_createTable.Checked && _loadMode.SelectedIndex != 2)
                {
                    MessageBox.Show(this, $"The table \"{_table.Text}\" does not exist. Tick \"Create the table\" to create it.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                FillMapping();
                return true;
            case 2:
                if (ReadMapping().Count == 0)
                {
                    MessageBox.Show(this, "Map at least one column.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                _job.ColumnMappings = ReadMapping();
                return true;
            default:
                return true;
        }
    }

    protected override async Task OnPageShownAsync(int index)
    {
        if (index == 0)
        {
            await LoadSheetsAsync();
            await RefreshPreviewAsync();
        }
        else if (index == 1 && _table.Items.Count == 0)
        {
            await LoadTablesAsync();
        }
        else if (index == 3)
        {
            SaveToJob();
            _summary.Text = Describe();
        }
    }

    private string Describe()
    {
        var lines = new List<string>
        {
            $"File:       {_job.SourcePath}",
            $"Format:     {_job.SourceFormat}",
            $"Database:   {_job.DatabasePath}",
            $"Table:      {_job.TargetTable}{(_targetColumns.Count == 0 ? " (will be created)" : string.Empty)}",
            $"Load mode:  {_loadMode.Text}",
            $"Bad rows:   {_errorMode.Text}",
            string.Empty,
            "Columns:",
        };
        lines.AddRange((_job.ColumnMappings ?? []).Select(m => m.Source is null ? $"  {m.Target} = '{m.Default}'" : $"  {m.Source} -> {m.Target}"));
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Run and save ---------------------------------------------------------------------

    private async Task RunImportAsync()
    {
        SaveToJob();
        var warning = _job.LoadMode switch
        {
            LoadMode.Truncate when _targetColumns.Count > 0 => $"This deletes every row of \"{_job.TargetTable}\" before importing.",
            LoadMode.Replace when _targetColumns.Count > 0 => $"This drops the table \"{_job.TargetTable}\" (with all its rows, indexes, and triggers) and creates it again from the file.",
            _ => null,
        };
        if (warning is not null && MessageBox.Show(this, warning + "\n\nContinue?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _run.Enabled = _saveJob.Enabled = BackButton.Enabled = false;
        _cancelRun.Enabled = true;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Importing...";
        try
        {
            var (manager, session) = await OpenAsync(_job.DatabasePath, _log, readOnly: false);
            await using (manager)
            {
                var progress = new Progress<long>(n => _status.Text = $"Importing... {n:N0} row(s) read");
                var result = await ImportEngine.RunAsync(session, _job, progress, _cts.Token);
                _status.Text = $"Done: {result.RowsRead:N0} read, {result.RowsWritten:N0} written, {result.Skipped:N0} skipped.";
                _summary.AppendText(Environment.NewLine + Environment.NewLine + _status.Text +
                    (result.ErrorFile is null ? string.Empty : $"{Environment.NewLine}Skipped rows are listed in {result.ErrorFile}"));
            }

            Imported?.Invoke(this, Path.GetFullPath(_job.DatabasePath));
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled. Nothing was imported.";
            _status.ForeColor = Theme.ErrorText;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Failed. Nothing was imported.";
            _status.ForeColor = Theme.ErrorText;
            _summary.AppendText(Environment.NewLine + Environment.NewLine + "Error: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _run.Enabled = _saveJob.Enabled = BackButton.Enabled = true;
            _cancelRun.Enabled = false;
        }
    }

    private async Task SaveJobAsync()
    {
        SaveToJob();
        if (!_editingJob)
        {
            var name = InputDialog.Ask(this, "Save as Job", "Job name:", _jobs.UniqueName($"Import {Path.GetFileNameWithoutExtension(_job.SourcePath)} to {_job.TargetTable}"));
            if (name is null)
            {
                return;
            }

            _job.Name = name;
        }

        try
        {
            await _jobs.SaveJobAsync(_job);
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = $"Saved job \"{_job.Name}\".";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Save Job", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts is not null)
        {
            e.Cancel = true; // Wait for the import to stop.
            _cts.Cancel();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewDelay.Dispose();
        }

        base.Dispose(disposing);
    }
}
