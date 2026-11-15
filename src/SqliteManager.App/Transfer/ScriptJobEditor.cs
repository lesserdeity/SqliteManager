using SqliteManager.App.Documents;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;

namespace SqliteManager.App.Transfer;

/// <summary>Edits a script job: database, transaction mode, parameters, and its steps (SQL or "run another job").</summary>
internal sealed class ScriptJobEditor : Form
{
    private readonly JobsService _jobs;
    private readonly ScriptJob _job;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _database;
    private readonly Control _databasePicker;
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly DataGridView _parameters = new()
    {
        Dock = DockStyle.Fill,
        RowHeadersVisible = false,
        BackgroundColor = Theme.WindowBack,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly ListBox _steps = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _stepName = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _stepJob = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly SqlEditor _stepSql = new() { Dock = DockStyle.Fill };
    private readonly TextBox _stepFile = new() { Dock = DockStyle.Fill, PlaceholderText = "Or a .sql file to run instead" };
    private readonly ComboBox _stepOnError = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _stepTimeout = new() { Width = 90, Maximum = 86_400 };
    private readonly TableLayoutPanel _stepDetails;
    private readonly List<ScriptStep> _stepList;
    private bool _loading;

    public ScriptJobEditor(JobsService jobs, IEnumerable<DatabaseSession> openSessions, ScriptJob? job)
    {
        _jobs = jobs;
        _job = job is null
            ? new ScriptJob { Name = jobs.UniqueName("New script job"), DatabasePath = openSessions.FirstOrDefault()?.FilePath ?? string.Empty }
            : (ScriptJob)job.Duplicate(job.Name);
        if (job is not null)
        {
            _job.Id = job.Id; // Editing keeps the same job.
        }

        _stepList = [.. _job.Steps];
        (_databasePicker, _database) = DatabasePicker(openSessions);

        Text = job is null ? "New Script Job" : $"Edit Script Job: {job.Name}";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(700));

        _mode.Items.AddRange(["Single: all steps in one transaction (a failing step rolls back all)", "None: each statement is saved as it runs"]);
        _stepOnError.Items.AddRange(["Stop the job (and roll back)", "Continue with the next step"]);
        _stepJob.Items.AddRange([.. jobs.File.Jobs.Where(j => j.Id != _job.Id).Select(j => j.Name)]);
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parameter (e.g. @Period)", FillWeight = 30 });
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Prompt", FillWeight = 45 });
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Default (empty = required)", FillWeight = 25 });

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(header, "Name:", _name);
        AddRow(header, "Database:", _databasePicker);
        AddRow(header, "Transaction:", _mode);

        var parametersBox = new GroupBox { Text = "Parameters", Dock = DockStyle.Top, Height = LogicalToDeviceUnits(120), Padding = new Padding(6) };
        parametersBox.Controls.Add(_parameters);

        // Steps: list on the left, details on the right.
        var addSql = new Button { Text = "+ SQL step", AutoSize = true };
        var addJob = new Button { Text = "+ Run job", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        var up = new Button { Text = "↑", Width = 32 };
        var down = new Button { Text = "↓", Width = 32 };
        addSql.Click += (_, _) => AddStep(new SqlStep { Name = $"Step {_stepList.Count + 1}" });
        addJob.Click += (_, _) => AddStep(new RunJobStep { Name = $"Step {_stepList.Count + 1}", Job = _stepJob.Items.Count > 0 ? (string)_stepJob.Items[0]! : string.Empty });
        remove.Click += (_, _) => RemoveStep();
        up.Click += (_, _) => MoveStep(-1);
        down.Click += (_, _) => MoveStep(1);
        var stepButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        stepButtons.Controls.AddRange([addSql, addJob, remove, up, down]);
        var left = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(280), Padding = new Padding(0, 0, 6, 0) };
        left.Controls.Add(_steps);
        left.Controls.Add(stepButtons);

        _stepDetails = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        _stepDetails.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _stepDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(_stepDetails, "Step name:", _stepName);
        AddRow(_stepDetails, "Job to run:", _stepJob);
        AddRow(_stepDetails, "If it fails:", _stepOnError);
        AddRow(_stepDetails, "Timeout (s):", _stepTimeout);
        AddRow(_stepDetails, "SQL file:", _stepFile);
        _stepDetails.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _stepDetails.Controls.Add(new Label { Text = "SQL:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, 5);
        _stepDetails.Controls.Add(_stepSql, 1, 5);

        var stepsBox = new GroupBox { Text = "Steps (run in order)", Dock = DockStyle.Fill, Padding = new Padding(6) };
        stepsBox.Controls.Add(_stepDetails);
        stepsBox.Controls.Add(left);

        var ok = new Button { Text = "Save", Width = 90, Dock = DockStyle.Right };
        var cancel = new Button { Text = "Cancel", Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        ok.Click += async (_, _) => await SaveAsync();
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(42), Padding = new Padding(8) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(stepsBox);
        Controls.Add(parametersBox);
        Controls.Add(header);
        Controls.Add(buttons);

        _steps.SelectedIndexChanged += (_, _) => ShowStep();
        foreach (var control in new Control[] { _stepName, _stepFile, _stepSql })
        {
            control.TextChanged += (_, _) => StoreStep();
        }

        _stepJob.SelectedIndexChanged += (_, _) => StoreStep();
        _stepOnError.SelectedIndexChanged += (_, _) => StoreStep();
        _stepTimeout.ValueChanged += (_, _) => StoreStep();

        _name.Text = _job.Name;
        _database.Text = _job.DatabasePath;
        _mode.SelectedIndex = (int)_job.TransactionMode;
        foreach (var p in _job.Parameters ?? [])
        {
            _parameters.Rows.Add(p.Name, p.Prompt ?? string.Empty, p.Default ?? string.Empty);
        }

        RefreshStepList();
        if (_stepList.Count > 0)
        {
            _steps.SelectedIndex = 0;
        }
        else
        {
            ShowStep();
        }
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control input)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) });
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(input);
    }

    private static (Control, ComboBox) DatabasePicker(IEnumerable<DatabaseSession> sessions)
    {
        var box = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        box.Items.AddRange([.. sessions.Select(s => s.FilePath)]);
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Right, Width = 90 };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|All files (*.*)|*.*" };
            if (dialog.ShowDialog(box.FindForm()) == DialogResult.OK)
            {
                box.Text = dialog.FileName;
            }
        };
        var panel = new Panel { Height = 26 };
        panel.Controls.Add(box);
        panel.Controls.Add(browse);
        return (panel, box);
    }

    private ScriptStep? Current => _steps.SelectedIndex >= 0 ? _stepList[_steps.SelectedIndex] : null;

    private void AddStep(ScriptStep step)
    {
        _stepList.Add(step);
        RefreshStepList();
        _steps.SelectedIndex = _stepList.Count - 1;
        _stepName.Focus();
    }

    private void RemoveStep()
    {
        if (_steps.SelectedIndex < 0)
        {
            return;
        }

        var index = _steps.SelectedIndex;
        _stepList.RemoveAt(index);
        RefreshStepList();
        _steps.SelectedIndex = Math.Min(index, _stepList.Count - 1);
        ShowStep();
    }

    private void MoveStep(int delta)
    {
        var index = _steps.SelectedIndex;
        var to = index + delta;
        if (index < 0 || to < 0 || to >= _stepList.Count)
        {
            return;
        }

        (_stepList[index], _stepList[to]) = (_stepList[to], _stepList[index]);
        RefreshStepList();
        _steps.SelectedIndex = to;
    }

    private void RefreshStepList()
    {
        _loading = true;
        var selected = _steps.SelectedIndex;
        _steps.Items.Clear();
        foreach (var step in _stepList)
        {
            _steps.Items.Add(step is RunJobStep r ? $"{step.Name}   (run job: {r.Job})" : $"{step.Name}   (SQL)");
        }

        if (selected >= 0 && selected < _steps.Items.Count)
        {
            _steps.SelectedIndex = selected;
        }

        _loading = false;
    }

    private void ShowStep()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var step = Current;
            _stepDetails.Enabled = step is not null;
            _stepName.Text = step?.Name ?? string.Empty;
            var isSql = step is SqlStep;
            _stepJob.Enabled = step is RunJobStep;
            _stepSql.Enabled = _stepFile.Enabled = _stepTimeout.Enabled = isSql;
            _stepJob.SelectedItem = (step as RunJobStep)?.Job;
            _stepSql.ReadOnly = false;
            _stepSql.Text = (step as SqlStep)?.Sql ?? string.Empty;
            _stepSql.ReadOnly = !isSql;
            _stepFile.Text = (step as SqlStep)?.SqlFile ?? string.Empty;
            _stepTimeout.Value = (step as SqlStep)?.TimeoutSeconds ?? 0;
            _stepOnError.SelectedIndex = step switch
            {
                SqlStep s => (int)s.OnError,
                RunJobStep r => (int)(r.OnError ?? StepErrorMode.Stop),
                _ => 0,
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private void StoreStep()
    {
        if (_loading || Current is not { } step)
        {
            return;
        }

        step.Name = _stepName.Text.Trim();
        var onError = (StepErrorMode)Math.Max(0, _stepOnError.SelectedIndex);
        switch (step)
        {
            case SqlStep sql:
                sql.Sql = string.IsNullOrWhiteSpace(_stepSql.Text) ? null : _stepSql.Text;
                sql.SqlFile = string.IsNullOrWhiteSpace(_stepFile.Text) ? null : _stepFile.Text.Trim();
                sql.TimeoutSeconds = _stepTimeout.Value > 0 ? (int)_stepTimeout.Value : null;
                sql.OnError = onError;
                break;
            case RunJobStep run:
                run.Job = _stepJob.SelectedItem as string ?? string.Empty;
                run.OnError = onError == StepErrorMode.Stop ? null : onError;
                break;
        }

        var index = _steps.SelectedIndex;
        _loading = true;
        _steps.Items[index] = step is RunJobStep r ? $"{step.Name}   (run job: {r.Job})" : $"{step.Name}   (SQL)";
        _loading = false;
    }

    private async Task SaveAsync()
    {
        _parameters.EndEdit();
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            problems.Add("Enter a job name.");
        }

        if (string.IsNullOrWhiteSpace(_database.Text))
        {
            problems.Add("Choose the database.");
        }

        if (_stepList.Count == 0)
        {
            problems.Add("Add at least one step.");
        }

        foreach (var step in _stepList)
        {
            if (step is SqlStep { Sql: null, SqlFile: null })
            {
                problems.Add($"Step \"{step.Name}\" has no SQL.");
            }
            else if (step is RunJobStep { Job: "" })
            {
                problems.Add($"Step \"{step.Name}\" has no job to run.");
            }
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _job.Name = _name.Text.Trim();
        _job.DatabasePath = _database.Text.Trim();
        _job.TransactionMode = (TransactionMode)Math.Max(0, _mode.SelectedIndex);
        _job.Steps = [.. _stepList];
        var parameters = _parameters.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && !string.IsNullOrWhiteSpace(r.Cells[0].Value as string))
            .Select(r => new JobParameter { Name = JobRunner.Normalize((string)r.Cells[0].Value!), Prompt = r.Cells[1].Value as string, Default = r.Cells[2].Value as string ?? string.Empty })
            .ToList();
        _job.Parameters = parameters.Count == 0 ? null : parameters;
        try
        {
            await _jobs.SaveJobAsync(_job);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Save Job", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
