using SqliteManager.App.Documents;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Transfer;

namespace SqliteManager.App.Transfer;

/// <summary>Export wizard: table or SQL (with parameters), format and options, file, run, save as job.</summary>
internal sealed class ExportWizard : WizardForm
{
    private readonly JobsService _jobs;
    private readonly ISqlLog _log;
    private readonly ExportJob _job;
    private readonly bool _editingJob;

    private readonly ComboBox _database;
    private readonly Control _databasePicker;
    private readonly RadioButton _fromTable = new() { Text = "A table or view:", AutoSize = true, Checked = true };
    private readonly RadioButton _fromSql = new() { Text = "The result of this SQL:", AutoSize = true };
    private readonly ComboBox _table = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly SqlEditor _sql = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _parameters = new()
    {
        Dock = DockStyle.Bottom,
        Height = 110,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        BackgroundColor = Theme.WindowBack,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _header = new() { Text = "Include column names (header row)", AutoSize = true, Checked = true };
    private readonly ComboBox _delimiter = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 80 };
    private readonly ComboBox _quoting = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _sheet = new() { Width = 200 };
    private readonly CheckBox _autoFit = new() { Text = "Auto-fit column widths", AutoSize = true, Checked = true };
    private readonly CheckBox _freeze = new() { Text = "Freeze the header row", AutoSize = true, Checked = true };
    private readonly TextBox _sqlTable = new() { Width = 200, PlaceholderText = "Table name in INSERTs" };
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _ifExists = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    private readonly TextBox _summary = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.WindowBack, Font = new Font("Consolas", 9.5f) };
    private readonly Button _run = new() { Text = "Run Export", Width = 120, Height = 30 };
    private readonly Button _cancelRun = new() { Text = "Cancel", Width = 90, Height = 30, Enabled = false };
    private readonly Button _saveJob = new() { Text = "Save as Job...", Width = 120, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private CancellationTokenSource? _cts;
    private bool _loading;

    public ExportWizard(JobsService jobs, ISqlLog log, IEnumerable<DatabaseSession> openSessions, ExportJob? job = null, string? databasePath = null, string? table = null, string? sql = null)
        : base(job is null ? "Export Data" : $"Edit Export Job: {job.Name}")
    {
        _jobs = jobs;
        _log = log;
        _editingJob = job is not null;
        _job = job ?? new ExportJob { DatabasePath = databasePath ?? string.Empty, Table = sql is null ? table : null, Sql = sql, Format = ExportFormat.Csv, IfFileExists = IfFileExists.Prompt };
        (_databasePicker, _database) = DatabasePicker(openSessions);

        _format.Items.AddRange(["CSV (comma separated)", "TSV (tab separated)", "Excel workbook (.xlsx)", "JSON (array of objects)", "SQL INSERT script"]);
        _delimiter.Items.AddRange([",", ";", "Tab", "|"]);
        _quoting.Items.AddRange(["Quote when needed", "Quote every field", "Never quote"]);
        _encoding.Items.AddRange([.. TextEncodings.Names]);
        _ifExists.Items.AddRange(["Ask me (fails on the command line)", "Replace the file", "Fail"]);

        AddPage("Source", BuildSourcePage());
        AddPage("Format and file", BuildFormatPage());
        AddPage("Run", BuildRunPage());
        LoadFromJob();
    }

    private Control BuildSourcePage()
    {
        _database.TextChanged += async (_, _) =>
        {
            if (!_loading)
            {
                await LoadTablesAsync();
            }
        };
        // The two choices sit in different containers, so keep them exclusive here.
        _fromTable.CheckedChanged += (_, _) =>
        {
            if (_fromTable.Checked)
            {
                _fromSql.Checked = false;
            }

            UpdateSourceState();
        };
        _fromSql.CheckedChanged += (_, _) =>
        {
            if (_fromSql.Checked)
            {
                _fromTable.Checked = false;
            }

            UpdateSourceState();
        };
        _sql.TextChanged += (_, _) => UpdateParameters();
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parameter", ReadOnly = true, FillWeight = 25 });
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Prompt (shown when the job runs)", FillWeight = 45 });
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Default value", FillWeight = 30 });

        var tableRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        tableRow.Controls.AddRange([_fromTable, _table]);
        var page = new Panel();
        page.Controls.Add(_sql);
        page.Controls.Add(new Label { Dock = DockStyle.Top, Height = 6 });
        page.Controls.Add(_fromSql);
        page.Controls.Add(tableRow);
        page.Controls.Add(Form(("Database:", _databasePicker)));
        page.Controls.Add(_parameters);
        page.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 22, Text = "Parameters used in the SQL (such as @StartDate). You are asked for their values when the export runs.", ForeColor = Theme.RibbonGroupText });
        _fromSql.Dock = DockStyle.Top;
        _sql.BringToFront();
        return page;
    }

    private Control BuildFormatPage()
    {
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Right, Width = 90 };
        browse.Click += (_, _) => BrowsePath();
        var pathRow = new Panel { Height = 26, Dock = DockStyle.Fill };
        pathRow.Controls.Add(_path);
        pathRow.Controls.Add(browse);
        _format.SelectedIndexChanged += (_, _) => UpdateFormatState();

        var page = new Panel();
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            ForeColor = Theme.RibbonGroupText,
            Text = "The file name can contain {date} (2026-10-31), {datetime} (2026-10-31_140509), and {jobname}. " +
                   "The file is replaced only when the export succeeds.",
        });
        page.Controls.Add(Form(
            ("Format:", _format),
            (string.Empty, _header),
            ("Delimiter:", _delimiter),
            ("Quoting:", _quoting),
            ("Encoding:", _encoding),
            ("Sheet name:", _sheet),
            (string.Empty, _autoFit),
            (string.Empty, _freeze),
            ("INSERT table:", _sqlTable),
            ("File:", pathRow),
            ("If the file exists:", _ifExists)));
        return page;
    }

    private Control BuildRunPage()
    {
        _run.Click += async (_, _) => await RunExportAsync();
        _cancelRun.Click += (_, _) => _cts?.Cancel();
        _saveJob.Click += async (_, _) => await SaveJobAsync();
        if (_editingJob)
        {
            _saveJob.Text = "Save Job";
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_run, _cancelRun, _saveJob, _status]);
        var page = new Panel();
        page.Controls.Add(_summary);
        page.Controls.Add(buttons);
        return page;
    }

    private void LoadFromJob()
    {
        _loading = true;
        try
        {
            var o = _job.Options ?? new ExportOptions();
            _database.Text = _job.DatabasePath;
            _fromSql.Checked = !string.IsNullOrWhiteSpace(_job.Sql);
            _fromTable.Checked = !_fromSql.Checked;
            _sql.Text = _job.Sql ?? string.Empty;
            _format.SelectedIndex = (int)_job.Format;
            _header.Checked = o.IncludeHeader ?? true;
            _delimiter.Text = o.Delimiter is "\t" ? "Tab" : o.Delimiter ?? (_job.Format == ExportFormat.Tsv ? "Tab" : ",");
            _quoting.SelectedIndex = (int)(o.Quoting ?? CsvQuoting.AsNeeded);
            _encoding.SelectedItem = o.Encoding is { } e && TextEncodings.Names.Contains(e) ? e : "utf-8";
            _sheet.Text = o.SheetName ?? string.Empty;
            _autoFit.Checked = o.AutoFit ?? true;
            _freeze.Checked = o.FreezeHeader ?? true;
            _sqlTable.Text = o.TableName ?? string.Empty;
            _path.Text = _job.OutputPath;
            _ifExists.SelectedIndex = _job.IfFileExists switch
            {
                IfFileExists.Overwrite => 1,
                IfFileExists.Fail => 2,
                _ => 0,
            };
            UpdateParameters();
            UpdateSourceState();
            UpdateFormatState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveToJob()
    {
        _parameters.EndEdit();
        _job.DatabasePath = _database.Text.Trim();
        _job.Sql = _fromSql.Checked ? _sql.Text.Trim() : null;
        _job.Table = _fromTable.Checked ? _table.SelectedItem as string : null;
        _job.Format = (ExportFormat)Math.Max(0, _format.SelectedIndex);
        var isText = _job.Format is ExportFormat.Csv or ExportFormat.Tsv;
        _job.Options = new ExportOptions
        {
            IncludeHeader = _job.Format is ExportFormat.Csv or ExportFormat.Tsv or ExportFormat.Xlsx ? _header.Checked : null,
            Delimiter = isText ? (_delimiter.Text == "Tab" ? "\t" : _delimiter.Text) : null,
            Quoting = isText ? (CsvQuoting)Math.Max(0, _quoting.SelectedIndex) : null,
            Encoding = _job.Format is ExportFormat.Xlsx or ExportFormat.Json ? null : _encoding.SelectedItem as string,
            SheetName = _job.Format == ExportFormat.Xlsx && _sheet.Text.Trim().Length > 0 ? _sheet.Text.Trim() : null,
            AutoFit = _job.Format == ExportFormat.Xlsx ? _autoFit.Checked : null,
            FreezeHeader = _job.Format == ExportFormat.Xlsx ? _freeze.Checked : null,
            TableName = _job.Format == ExportFormat.Sql && _sqlTable.Text.Trim().Length > 0 ? _sqlTable.Text.Trim() : null,
        };
        _job.OutputPath = _path.Text.Trim();
        _job.IfFileExists = _ifExists.SelectedIndex switch
        {
            1 => IfFileExists.Overwrite,
            2 => IfFileExists.Fail,
            _ => IfFileExists.Prompt,
        };
        var parameters = _parameters.Rows.Cast<DataGridViewRow>()
            .Select(r => new JobParameter { Name = (string)r.Cells[0].Value!, Prompt = NullIfEmpty(r.Cells[1].Value as string), Default = r.Cells[2].Value as string ?? string.Empty })
            .ToList();
        _job.Parameters = parameters.Count == 0 ? null : parameters;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void UpdateSourceState()
    {
        _table.Enabled = _fromTable.Checked;
        _sql.ReadOnly = !_fromSql.Checked;
        _sql.Enabled = _fromSql.Checked;
        UpdateParameters();
    }

    /// <summary>Lists the @, :, and $ parameters used in the SQL, keeping prompts and defaults already typed.</summary>
    private void UpdateParameters()
    {
        var names = new List<string>();
        if (_fromSql.Checked)
        {
            try
            {
                names = SqlTokenizer.Tokenize(_sql.Text).Where(t => t.Kind == SqlTokenKind.Variable && t.Text.Length > 1)
                    .Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (SqlParseException)
            {
                return; // Keep the list while the SQL is being typed.
            }
        }

        var existing = _parameters.Rows.Cast<DataGridViewRow>().ToDictionary(r => (string)r.Cells[0].Value!, r => (r.Cells[1].Value as string, r.Cells[2].Value as string), StringComparer.OrdinalIgnoreCase);
        foreach (var p in _job.Parameters ?? [])
        {
            existing.TryAdd(JobRunner.Normalize(p.Name), (p.Prompt, p.Default));
        }

        _parameters.Rows.Clear();
        foreach (var name in names)
        {
            var (prompt, value) = existing.GetValueOrDefault(name);
            _parameters.Rows.Add(name, prompt ?? string.Empty, value ?? string.Empty);
        }
    }

    private void UpdateFormatState()
    {
        var format = (ExportFormat)Math.Max(0, _format.SelectedIndex);
        var isText = format is ExportFormat.Csv or ExportFormat.Tsv;
        _delimiter.Enabled = _quoting.Enabled = isText;
        _encoding.Enabled = format is ExportFormat.Csv or ExportFormat.Tsv or ExportFormat.Sql;
        _header.Enabled = isText || format == ExportFormat.Xlsx;
        _sheet.Enabled = _autoFit.Enabled = _freeze.Enabled = format == ExportFormat.Xlsx;
        _sqlTable.Enabled = format == ExportFormat.Sql;
        if (!_loading && format == ExportFormat.Tsv)
        {
            _delimiter.Text = "Tab";
        }
        else if (!_loading && format == ExportFormat.Csv && _delimiter.Text == "Tab")
        {
            _delimiter.Text = ",";
        }

        // Keep the file extension in step with the format.
        if (!_loading && _path.Text.Length > 0)
        {
            _path.Text = Path.ChangeExtension(_path.Text, ExportEngine.Extension(format));
        }
    }

    private void BrowsePath()
    {
        var format = (ExportFormat)Math.Max(0, _format.SelectedIndex);
        var extension = ExportEngine.Extension(format);
        using var dialog = new SaveFileDialog
        {
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} files (*{extension})|*{extension}|All files (*.*)|*.*",
            OverwritePrompt = false, // "If the file exists" decides, when the export runs.
            FileName = string.IsNullOrWhiteSpace(_path.Text) ? (_job.Table ?? "export") + extension : Path.GetFileName(_path.Text),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.FileName;
        }
    }

    private async Task LoadTablesAsync()
    {
        var path = _database.Text.Trim();
        _table.Items.Clear();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            List<string> names;
            var (manager, session) = await OpenAsync(path, _log, readOnly: true);
            await using (manager)
            {
                names = await session.QueryAsync("SELECT name FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0));
                _table.Items.AddRange([.. names]);
            }

            _table.SelectedItem = names.FirstOrDefault(n => n.Equals(_job.Table, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Cannot read the database: " + ex.Message;
        }
    }

    protected override async Task OnPageShownAsync(int index)
    {
        if (index == 0 && _table.Items.Count == 0)
        {
            if (_database.Text.Length == 0 && _database.Items.Count > 0)
            {
                _database.SelectedIndex = 0;
            }

            await LoadTablesAsync();
        }
        else if (index == 1 && string.IsNullOrWhiteSpace(_path.Text))
        {
            var name = _fromTable.Checked && _table.SelectedItem is string t ? t : "export";
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _path.Text = Path.Combine(folder, $"{name}_{{date}}{ExportEngine.Extension((ExportFormat)Math.Max(0, _format.SelectedIndex))}");
        }
        else if (index == 2)
        {
            SaveToJob();
            _summary.Text = string.Join(Environment.NewLine,
                $"Database:  {_job.DatabasePath}",
                $"Source:    {(_job.Sql is not null ? "SQL" : _job.Table)}",
                $"Format:    {_format.Text}",
                $"File:      {_job.OutputPath}",
                $"           ({ExportEngine.ResolvePath(_job.OutputPath, _job.Name, DateTime.Now)} if run now)",
                $"If exists: {_ifExists.Text}",
                _job.Sql is null ? string.Empty : Environment.NewLine + _job.Sql);
        }
    }

    protected override Task<bool> CanLeavePageAsync(int index)
    {
        var message = index switch
        {
            0 when !File.Exists(_database.Text.Trim()) => "Choose an existing database file.",
            0 when _fromTable.Checked && _table.SelectedItem is null => "Choose a table or view.",
            0 when _fromSql.Checked && string.IsNullOrWhiteSpace(_sql.Text) => "Enter the SQL to export.",
            1 when string.IsNullOrWhiteSpace(_path.Text) => "Choose the file to write.",
            _ => null,
        };
        if (message is not null)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return Task.FromResult(message is null);
    }

    private async Task RunExportAsync()
    {
        SaveToJob();
        if (string.IsNullOrWhiteSpace(_job.Name))
        {
            _job.Name = _job.Table ?? "export";
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (_job.Parameters is { Count: > 0 } parameters)
        {
            using var dialog = new ParameterDialog(_job.Name, parameters);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            foreach (var (name, value) in dialog.Values)
            {
                values[name] = value;
            }
        }

        _cts = new CancellationTokenSource();
        _run.Enabled = _saveJob.Enabled = BackButton.Enabled = false;
        _cancelRun.Enabled = true;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Exporting...";
        try
        {
            var (manager, session) = await OpenAsync(_job.DatabasePath, _log, readOnly: true);
            await using (manager)
            {
                var progress = new Progress<long>(n => _status.Text = $"Exporting... {n:N0} row(s)");
                bool? Confirm(string path) => (bool)Invoke(() => MessageBox.Show(this, $"The file \"{path}\" already exists.\n\nReplace it?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
                var result = await ExportEngine.RunAsync(session, _job, values, Confirm, progress, _cts.Token);
                _status.Text = $"Done: {result.Rows:N0} row(s) written.";
                _summary.AppendText($"{Environment.NewLine}{Environment.NewLine}Wrote {result.Rows:N0} row(s) to {result.FilePath}");
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled. No file was written.";
            _status.ForeColor = Theme.ErrorText;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Failed. No file was written.";
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
            var name = InputDialog.Ask(this, "Save as Job", "Job name:", _jobs.UniqueName($"Export {_job.Table ?? "query"}"));
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
            e.Cancel = true;
            _cts.Cancel();
            return;
        }

        base.OnFormClosing(e);
    }
}
