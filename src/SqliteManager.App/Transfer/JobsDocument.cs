using System.Globalization;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Jobs;

namespace SqliteManager.App.Transfer;

/// <summary>The Jobs tab: saved jobs (create, edit, duplicate, delete, run) and their run history.</summary>
internal sealed class JobsDocument : UserControl
{
    private readonly JobsService _jobs;
    private readonly Func<IReadOnlyList<DatabaseSession>> _openSessions;
    private readonly Action<string, bool> _report;
    private readonly ListView _list;
    private readonly ListView _history;
    private readonly ToolStripButton _edit;
    private readonly ToolStripButton _duplicate;
    private readonly ToolStripButton _delete;
    private readonly ToolStripButton _run;
    private readonly ToolStripButton _runAll;
    private readonly ToolStripButton _cancel;
    private readonly ToolStripLabel _status = new() { Alignment = ToolStripItemAlignment.Right };
    private IReadOnlyList<RunRecord> _records = [];
    private CancellationTokenSource? _cts;

    public JobsDocument(JobsService jobs, Func<IReadOnlyList<DatabaseSession>> openSessions, Action<string, bool> report)
    {
        _jobs = jobs;
        _openSessions = openSessions;
        _report = report;

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
        _list.Columns.Add("Name", 260);
        _list.Columns.Add("Type", 70);
        _list.Columns.Add("Database", 300);
        _list.Columns.Add("Last run", 140);
        _list.Columns.Add("Result", 260);
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateButtons();
            ShowHistory();
        };
        _list.DoubleClick += async (_, _) => await EditAsync();

        _history = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, ShowItemToolTips = true };
        _history.Columns.Add("Started", 140);
        _history.Columns.Add("Job / step", 300);
        _history.Columns.Add("Rows", 80, HorizontalAlignment.Right);
        _history.Columns.Add("Seconds", 70, HorizontalAlignment.Right);
        _history.Columns.Add("Status", 80);
        _history.Columns.Add("Error", 500);

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new RibbonRenderer(), BackColor = Theme.RibbonBody, Padding = new Padding(4, 2, 4, 2) };
        var newImport = new ToolStripButton("New Import...", null, async (_, _) => await NewImportAsync());
        var newExport = new ToolStripButton("New Export...", null, async (_, _) => await NewExportAsync());
        var newScript = new ToolStripButton("New Script...", null, async (_, _) => await NewScriptAsync());
        _edit = new ToolStripButton("Edit...", null, async (_, _) => await EditAsync());
        _duplicate = new ToolStripButton("Duplicate", null, async (_, _) => await DuplicateAsync());
        _delete = new ToolStripButton("Delete", null, async (_, _) => await DeleteAsync());
        _run = new ToolStripButton("Run", null, async (_, _) => await RunSelectedAsync(onlyFirst: true)) { Font = new Font("Segoe UI Semibold", 9f) };
        _runAll = new ToolStripButton("Run Selected in Order", null, async (_, _) => await RunSelectedAsync(onlyFirst: false));
        _cancel = new ToolStripButton("Cancel", null, (_, _) => _cts?.Cancel()) { Enabled = false };
        var refresh = new ToolStripButton("Refresh", null, async (_, _) => await ReloadAsync());
        toolbar.Items.AddRange([newImport, newExport, newScript, new ToolStripSeparator(), _edit, _duplicate, _delete, new ToolStripSeparator(), _run, _runAll, _cancel, new ToolStripSeparator(), refresh, _status]);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_history);
        split.Panel2.Controls.Add(new Label { Text = "Run history (of the selected jobs, or all jobs)", Dock = DockStyle.Top, Height = 24, BackColor = Theme.PaneHeader, Padding = new Padding(6, 4, 0, 0) });

        Controls.Add(split);
        Controls.Add(toolbar);
        _jobs.Changed += (_, _) => RunOnUi(Populate);
        UpdateButtons();
    }

    public event EventHandler? StateChanged;

    public bool IsRunning => _cts is not null;

    public async Task ReloadAsync()
    {
        try
        {
            await _jobs.LoadAsync();
            _records = await _jobs.History.LoadAsync();
            Populate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _report($"Cannot read the jobs: {ex.Message}", true);
        }
    }

    private List<Job> Selected => _list.SelectedItems.Cast<ListViewItem>().OrderBy(i => i.Index).Select(i => (Job)i.Tag!).ToList();

    private void Populate()
    {
        var selected = Selected.Select(j => j.Id).ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var job in _jobs.File.Jobs)
        {
            var last = _records.FirstOrDefault(r => r.Job.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            var item = new ListViewItem([job.Name, job.TypeName, job.DatabasePath,
                last is null ? string.Empty : last.Start.ToString("g", CultureInfo.CurrentCulture),
                last is null ? string.Empty : last.Status == RunRecord.Succeeded ? $"OK, {last.Rows:N0} row(s)" : $"{last.Status}: {last.Error}"])
            {
                Tag = job,
                Selected = selected.Contains(job.Id),
            };
            if (last is { Status: not RunRecord.Succeeded })
            {
                item.SubItems[4].ForeColor = Theme.ErrorText;
                item.UseItemStyleForSubItems = false;
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();
        ShowHistory();
        UpdateButtons();
    }

    private void ShowHistory()
    {
        var names = Selected.Select(j => j.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _history.BeginUpdate();
        _history.Items.Clear();
        foreach (var r in _records.Where(r => names.Count == 0 || names.Contains(r.Job) || (r.Parent is not null && names.Contains(r.Parent))).Take(500))
        {
            var item = new ListViewItem([r.Start.ToString("g", CultureInfo.CurrentCulture), r.Job, r.Rows.ToString("N0", CultureInfo.CurrentCulture),
                (r.End - r.Start).TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture), r.Status, r.Error ?? string.Empty])
            {
                ToolTipText = r.Error ?? string.Empty,
            };
            if (r.Status != RunRecord.Succeeded)
            {
                item.ForeColor = Theme.ErrorText;
            }

            _history.Items.Add(item);
        }

        _history.EndUpdate();
    }

    private void UpdateButtons()
    {
        var count = _list.SelectedItems.Count;
        var running = IsRunning;
        _edit.Enabled = _duplicate.Enabled = count == 1 && !running;
        _delete.Enabled = count > 0 && !running;
        _run.Enabled = count == 1 && !running;
        _runAll.Enabled = count > 1 && !running;
        _cancel.Enabled = running;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task NewImportAsync() => ShowWizardAsync(new ImportWizard(_jobs, _jobs.Log, _openSessions()));

    public Task NewExportAsync() => ShowWizardAsync(new ExportWizard(_jobs, _jobs.Log, _openSessions()));

    private async Task NewScriptAsync()
    {
        using var editor = new ScriptJobEditor(_jobs, _openSessions(), null);
        editor.ShowDialog(this);
        await ReloadAsync();
    }

    private async Task EditAsync()
    {
        if (Selected is not [var job] || IsRunning)
        {
            return;
        }

        switch (job)
        {
            case ImportJob import:
                // Edit a copy, so Close without saving leaves the job unchanged.
                var importCopy = (ImportJob)import.Duplicate(import.Name);
                importCopy.Id = import.Id;
                await ShowWizardAsync(new ImportWizard(_jobs, _jobs.Log, _openSessions(), importCopy));
                break;
            case ExportJob export:
                var exportCopy = (ExportJob)export.Duplicate(export.Name);
                exportCopy.Id = export.Id;
                await ShowWizardAsync(new ExportWizard(_jobs, _jobs.Log, _openSessions(), exportCopy));
                break;
            case ScriptJob script:
                using (var editor = new ScriptJobEditor(_jobs, _openSessions(), script))
                {
                    editor.ShowDialog(this);
                }

                await ReloadAsync();
                break;
        }
    }

    private async Task ShowWizardAsync(Form wizard)
    {
        using (wizard)
        {
            wizard.ShowDialog(this);
        }

        await ReloadAsync();
    }

    private async Task DuplicateAsync()
    {
        if (Selected is not [var job])
        {
            return;
        }

        await SaveAsync(() => _jobs.SaveJobAsync(job.Duplicate(_jobs.UniqueName(job.Name + " copy"))));
    }

    private async Task DeleteAsync()
    {
        var jobs = Selected;
        if (jobs.Count == 0)
        {
            return;
        }

        var names = string.Join("\n", jobs.Select(j => "• " + j.Name));
        if (MessageBox.Show(this, $"Delete these jobs?\n\n{names}\n\nTheir run history is kept.", "Delete Jobs",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        foreach (var job in jobs)
        {
            await SaveAsync(() => _jobs.DeleteJobAsync(job));
        }
    }

    private async Task SaveAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Jobs", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Runs the selected job, or all selected jobs in list order (stopping at the first failure).</summary>
    private async Task RunSelectedAsync(bool onlyFirst)
    {
        var jobs = onlyFirst ? Selected.Take(1).ToList() : Selected;
        if (jobs.Count == 0)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            foreach (var job in jobs)
            {
                _status.Text = $"Running {job.Name}...";
                var result = await _jobs.RunAsync(this, job, _report, _cts.Token);
                if (result is null)
                {
                    _status.Text = "Not run.";
                    break;
                }

                _status.Text = result.Succeeded ? $"{job.Name}: OK, {result.Rows:N0} row(s)." : $"{job.Name}: {result.Error}";
                if (!result.Succeeded)
                {
                    break;
                }
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            await ReloadAsync();
            UpdateButtons();
        }
    }

    private void RunOnUi(Action action)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
