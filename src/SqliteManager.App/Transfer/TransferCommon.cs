using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Transfer;

namespace SqliteManager.App.Transfer;

/// <summary>The app's saved jobs and run history, shared by the wizards and the Jobs tab.</summary>
internal sealed class JobsService
{
    public JobsService(ISqlLog log)
    {
        Log = log;
    }

    public JobStore Store { get; } = new(JobStore.DefaultPath);

    public RunHistoryStore History { get; } = new(RunHistoryStore.DefaultPath);

    public ISqlLog Log { get; }

    public JobFile File { get; private set; } = new();

    /// <summary>Raised after jobs are loaded or saved.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a job ran (with the database path), so open views can refresh.</summary>
    public event EventHandler<string>? JobRan;

    public async Task LoadAsync()
    {
        File = await Store.LoadAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or replaces (by id) a job and saves the file. Reloads first so jobs saved by the CLI are kept.</summary>
    public async Task SaveJobAsync(Job job)
    {
        var file = await Store.LoadAsync();
        var index = file.Jobs.FindIndex(j => j.Id == job.Id);
        if (file.Jobs.Any(j => j.Id != job.Id && j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Another job is already named \"{job.Name}\".");
        }

        if (index >= 0)
        {
            file.Jobs[index] = job;
        }
        else
        {
            file.Jobs.Add(job);
        }

        await Store.SaveAsync(file);
        File = file;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteJobAsync(Job job)
    {
        var file = await Store.LoadAsync();
        file.Jobs.RemoveAll(j => j.Id == job.Id);
        await Store.SaveAsync(file);
        File = file;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; File.Find(name) is not null; i++)
        {
            name = $"{baseName} ({i})";
        }

        return name;
    }

    /// <summary>
    /// Runs a job from the app: asks for parameter values, confirms anything that deletes rows or replaces a
    /// file, then runs it on its own connection. Returns null if the user cancelled.
    /// </summary>
    public async Task<JobRunResult?> RunAsync(IWin32Window owner, Job job, Action<string, bool> report, CancellationToken cancellationToken = default)
    {
        var runner = new JobRunner(File, History, Log, new AppJobHost(owner, report));
        var parameters = new Dictionary<string, string>();
        var definitions = runner.ParametersOf(job);
        if (definitions.Count > 0)
        {
            using var dialog = new ParameterDialog(job.Name, definitions);
            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                return null;
            }

            parameters = dialog.Values;
        }

        var warnings = DestructiveActions(job, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (warnings.Count > 0 && MessageBox.Show(owner,
                $"Running \"{job.Name}\" will:\n\n{string.Join("\n", warnings.Select(w => "• " + w))}\n\nContinue?",
                "Run Job", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return null;
        }

        var result = await runner.RunAsync(job, parameters, cancellationToken);
        JobRan?.Invoke(this, Path.GetFullPath(job.DatabasePath));
        foreach (var child in ChildJobs(job))
        {
            JobRan?.Invoke(this, Path.GetFullPath(child.DatabasePath));
        }

        return result;
    }

    private IEnumerable<Job> ChildJobs(Job job) =>
        job is ScriptJob script ? script.Steps.OfType<RunJobStep>().Select(s => File.Find(s.Job)).OfType<Job>() : [];

    /// <summary>What the job (and jobs it calls) would delete or replace, for the confirmation.</summary>
    private List<string> DestructiveActions(Job job, HashSet<string> seen)
    {
        var result = new List<string>();
        if (!seen.Add(job.Name))
        {
            return result;
        }

        switch (job)
        {
            case ImportJob { LoadMode: LoadMode.Truncate } import:
                result.Add($"delete every row of \"{import.TargetTable}\" before importing ({import.Name})");
                break;
            case ImportJob { LoadMode: LoadMode.Replace } import:
                result.Add($"drop table \"{import.TargetTable}\" and create it again from the file ({import.Name})");
                break;
            case ExportJob { IfFileExists: IfFileExists.Overwrite } export when !string.IsNullOrWhiteSpace(export.OutputPath):
                var path = ExportEngine.ResolvePath(export.OutputPath, export.Name, DateTime.Now);
                if (System.IO.File.Exists(path))
                {
                    result.Add($"replace the existing file \"{path}\" ({export.Name})");
                }

                break;
            case ScriptJob:
                foreach (var child in ChildJobs(job))
                {
                    result.AddRange(DestructiveActions(child, seen));
                }

                break;
        }

        return result;
    }
}

/// <summary>Sends job messages to the main window and asks before replacing files.</summary>
internal sealed class AppJobHost(IWin32Window owner, Action<string, bool> report) : IJobHost
{
    public void Report(string message) => report(message, message.Contains("FAILED", StringComparison.Ordinal));

    public bool? ConfirmOverwrite(string path)
    {
        if (owner is not Control control)
        {
            return null;
        }

        return (bool)control.Invoke(() =>
            MessageBox.Show(owner, $"The file \"{path}\" already exists.\n\nReplace it?", "Export", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
    }
}

/// <summary>Asks for values of job parameters such as @Period.</summary>
internal sealed class ParameterDialog : Form
{
    private readonly DataGridView _grid;

    public ParameterDialog(string jobName, IReadOnlyList<JobParameter> parameters)
    {
        Text = $"Parameters for {jobName}";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(120 + (parameters.Count * 26)));

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Theme.WindowBack,
            BorderStyle = BorderStyle.None,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parameter", ReadOnly = true, FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", ReadOnly = true, FillWeight = 35 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", FillWeight = 35 });
        foreach (var p in parameters)
        {
            _grid.Rows.Add(JobRunner.Normalize(p.Name), p.Prompt ?? string.Empty, p.Default ?? string.Empty);
        }

        var ok = new Button { Text = "Run", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = LogicalToDeviceUnits(90) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = LogicalToDeviceUnits(90) };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(38), Padding = new Padding(6) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(_grid);
        Controls.Add(buttons);
        Shown += (_, _) =>
        {
            if (_grid.RowCount > 0)
            {
                _grid.CurrentCell = _grid[2, 0];
                _grid.BeginEdit(true);
            }
        };
        FormClosing += (_, e) =>
        {
            _grid.EndEdit();
            if (DialogResult == DialogResult.OK && _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => string.IsNullOrEmpty(r.Cells[2].Value as string)) is { } empty)
            {
                MessageBox.Show(this, $"Enter a value for {empty.Cells[0].Value}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        };
    }

    /// <summary>Enter runs the job even while a value is being edited (the grid would otherwise just move down a row).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            _grid.EndEdit();
            DialogResult = DialogResult.OK;
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    public Dictionary<string, string> Values =>
        _grid.Rows.Cast<DataGridViewRow>().ToDictionary(r => (string)r.Cells[0].Value!, r => r.Cells[2].Value as string ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A one-line text prompt (for example a job name).</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _text;

    private InputDialog(string title, string label, string value)
    {
        Text = title;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(100));
        var caption = new Label { Text = label, Location = new Point(LogicalToDeviceUnits(12), LogicalToDeviceUnits(12)), AutoSize = true };
        _text = new TextBox { Text = value, Location = new Point(LogicalToDeviceUnits(12), LogicalToDeviceUnits(34)), Width = LogicalToDeviceUnits(376) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(LogicalToDeviceUnits(216), LogicalToDeviceUnits(66)), Width = LogicalToDeviceUnits(80) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(LogicalToDeviceUnits(306), LogicalToDeviceUnits(66)), Width = LogicalToDeviceUnits(80) };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([caption, _text, ok, cancel]);
    }

    public static string? Ask(IWin32Window owner, string title, string label, string value)
    {
        using var dialog = new InputDialog(title, label, value);
        return dialog.ShowDialog(owner) == DialogResult.OK && dialog._text.Text.Trim().Length > 0 ? dialog._text.Text.Trim() : null;
    }
}

/// <summary>A simple wizard: one panel per page, with Back and Next.</summary>
internal abstract class WizardForm : Form
{
    private readonly List<(string Title, Control Page)> _pages = [];
    private readonly Panel _host;
    private readonly Label _title;
    private readonly Label _steps;
    private int _index;

    protected WizardForm(string title)
    {
        Text = title;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(700), LogicalToDeviceUnits(480));

        _title = new Label { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Accent, Padding = new Padding(10, 6, 0, 0) };
        _steps = new Label { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(22), ForeColor = Theme.RibbonGroupText, Padding = new Padding(12, 0, 0, 0) };
        _host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        BackButton = new Button { Text = "< Back", Width = LogicalToDeviceUnits(90), Dock = DockStyle.Right };
        NextButton = new Button { Text = "Next >", Width = LogicalToDeviceUnits(90), Dock = DockStyle.Right };
        CloseButton = new Button { Text = "Close", Width = LogicalToDeviceUnits(90), Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        BackButton.Click += (_, _) => Show(_index - 1);
        NextButton.Click += async (_, _) =>
        {
            if (await CanLeavePageAsync(_index))
            {
                Show(_index + 1);
            }
        };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(44), Padding = new Padding(8) };
        buttons.Controls.Add(BackButton);
        buttons.Controls.Add(NextButton);
        buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12 });
        buttons.Controls.Add(CloseButton);
        CancelButton = CloseButton;

        Controls.Add(_host);
        Controls.Add(_steps);
        Controls.Add(_title);
        Controls.Add(buttons);
    }

    protected Button BackButton { get; }

    protected Button NextButton { get; }

    protected Button CloseButton { get; }

    protected int PageIndex => _index;

    protected void AddPage(string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages.Add((title, page));
        _host.Controls.Add(page);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Show(0);
    }

    /// <summary>Checks the page before moving on; show a message and return false to stay.</summary>
    protected virtual Task<bool> CanLeavePageAsync(int index) => Task.FromResult(true);

    /// <summary>Called when a page is shown.</summary>
    protected virtual Task OnPageShownAsync(int index) => Task.CompletedTask;

    private async void Show(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        _pages[_index].Page.Visible = false;
        _index = index;
        _pages[index].Page.Visible = true;
        _title.Text = _pages[index].Title;
        _steps.Text = string.Join("   ›   ", _pages.Select((p, i) => i == index ? $"[{i + 1}. {p.Title}]" : $"{i + 1}. {p.Title}"));
        BackButton.Enabled = index > 0;
        NextButton.Enabled = index < _pages.Count - 1;
        try
        {
            await OnPageShownAsync(index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>A labeled two-column form layout.</summary>
    protected static TableLayoutPanel Form(params (string Label, Control Input)[] rows)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, input) in rows)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            input.Anchor = input is CheckBox ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(input);
        }

        return layout;
    }

    /// <summary>A read-only preview grid.</summary>
    protected static DataGridView PreviewGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        BackgroundColor = Theme.WindowBack,
        BorderStyle = BorderStyle.FixedSingle,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
    };

    /// <summary>Opens a database file on its own connection for the wizard (read-only unless writing).</summary>
    protected static async Task<(DatabaseManager Manager, DatabaseSession Session)> OpenAsync(string path, ISqlLog log, bool readOnly)
    {
        var manager = new DatabaseManager(log);
        try
        {
            return (manager, await manager.OpenAsync(path, readOnly));
        }
        catch
        {
            await manager.DisposeAsync();
            throw;
        }
    }

    /// <summary>A database path box with the open databases in its list and a Browse button.</summary>
    protected static (Control Container, ComboBox Box) DatabasePicker(IEnumerable<DatabaseSession> openSessions)
    {
        var box = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        box.Items.AddRange([.. openSessions.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase)]);
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Right, Width = 90 };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|All files (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(box.FindForm()) == DialogResult.OK)
            {
                box.Text = dialog.FileName;
            }
        };
        var panel = new Panel { Height = 26, Dock = DockStyle.Fill };
        panel.Controls.Add(box);
        panel.Controls.Add(browse);
        return (panel, box);
    }
}
