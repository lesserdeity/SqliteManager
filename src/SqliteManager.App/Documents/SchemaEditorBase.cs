using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.Documents;

/// <summary>Raised after an editor applied its changes.</summary>
internal sealed class SchemaAppliedEventArgs(SchemaObjectType type, string? oldName, string newName) : EventArgs
{
    public SchemaObjectType Type { get; } = type;

    /// <summary>The name before the change, or null if the object is new.</summary>
    public string? OldName { get; } = oldName;

    public string NewName { get; } = newName;
}

/// <summary>
/// Base for the table designer and the view and trigger editors: an editing area on top and a SQL preview
/// with a list of problems below. Apply runs the plan shown in the preview.
/// </summary>
internal abstract class SchemaEditorBase : UserControl
{
    private readonly SplitContainer _split;
    private readonly Label _summary;
    private readonly ListView _issues;
    private readonly SqlEditor _preview;
    private readonly System.Windows.Forms.Timer _previewDelay = new() { Interval = 250 };
    private AlterPlan _plan = AlterPlan.Empty;
    private IReadOnlyList<DesignIssue> _issueList = [];

    protected SchemaEditorBase(DatabaseSession session)
    {
        Session = session;

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(24),
            Padding = new Padding(LogicalToDeviceUnits(6), 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.PaneHeader,
            ForeColor = Theme.PaneHeaderText,
            AutoEllipsis = true,
        };
        _issues = new ListView
        {
            Dock = DockStyle.Top,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            Height = LogicalToDeviceUnits(64),
            BorderStyle = BorderStyle.None,
            ShowItemToolTips = true,
        };
        _issues.Columns.Add("Issue", 2000);
        _preview = new SqlEditor { Dock = DockStyle.Fill, ReadOnly = true };

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.WorkspaceBack };
        _split.Panel2.Controls.Add(_preview);
        _split.Panel2.Controls.Add(_issues);
        _split.Panel2.Controls.Add(_summary);
        Controls.Add(_split);

        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            UpdatePreview();
        };
    }

    public event EventHandler? StateChanged;

    public event EventHandler<SchemaAppliedEventArgs>? Applied;

    public event EventHandler<(string Text, bool IsError)>? Message;

    public DatabaseSession Session { get; }

    /// <summary>Text for the document tab, without the modified marker.</summary>
    public abstract string Title { get; }

    /// <summary>The kind of object this editor changes.</summary>
    public abstract SchemaObjectType ObjectType { get; }

    /// <summary>The object's name in the database, or null if it has not been created yet.</summary>
    public abstract string? OriginalName { get; }

    public bool IsModified => _plan.Kind != AlterKind.None;

    public bool CanApply => IsModified && !_issueList.Any(i => i.Severity == IssueSeverity.Error) && !IsReadOnly;

    /// <summary>True when the object could not be read safely and is shown for viewing only.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsReadOnly { get; protected set; }

    /// <summary>The editing area above the preview.</summary>
    protected Control EditorArea => _split.Panel1;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = Math.Max(LogicalToDeviceUnits(150), _split.Height * 60 / 100);
    }

    /// <summary>Reads the object from the database (or sets up a new one).</summary>
    public abstract Task LoadAsync();

    /// <summary>Builds the plan for the current edits and the problems with them.</summary>
    protected abstract (AlterPlan Plan, IReadOnlyList<DesignIssue> Issues, string Summary) BuildPlan();

    /// <summary>Puts the editor back to the object as it is in the database.</summary>
    public abstract void Revert();

    /// <summary>Called after the plan ran. Reloads from the database.</summary>
    protected abstract Task OnAppliedAsync();

    /// <summary>Gives the editor names for autocomplete in its SQL editors.</summary>
    public virtual void SetSchema(SchemaSnapshot schema) => _preview.SetSchema(schema);

    /// <summary>Finishes any cell or text edit in progress so it is part of the plan.</summary>
    protected virtual void CommitPendingEdits() => Validate();

    /// <summary>Asks before data or objects are lost. Returns false to cancel.</summary>
    protected virtual bool ConfirmApply(AlterPlan plan) => true;

    /// <summary>Schedules a preview update (called after each edit).</summary>
    protected void RequestPreview()
    {
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    protected void UpdatePreview()
    {
        _previewDelay.Stop();
        string summary;
        try
        {
            (_plan, _issueList, summary) = BuildPlan();
        }
        catch (Exception ex) when (ex is SqlParseException or InvalidOperationException or ArgumentException)
        {
            (_plan, _issueList, summary) = (AlterPlan.Empty, [new DesignIssue(IssueSeverity.Error, ex.Message)], "Cannot build the SQL.");
        }

        _summary.Text = summary;
        _preview.ReadOnly = false;
        _preview.Text = _plan.Steps.Count == 0 ? "-- No changes." : _plan.ToScript();
        _preview.ReadOnly = true;

        _issues.BeginUpdate();
        _issues.Items.Clear();
        foreach (var issue in _issueList)
        {
            var item = _issues.Items.Add((issue.Severity == IssueSeverity.Error ? "✕  " : "⚠  ") + issue.Message);
            item.ForeColor = issue.Severity == IssueSeverity.Error ? Theme.ErrorText : Theme.WarningText;
            item.ToolTipText = issue.Message;
        }

        _issues.EndUpdate();
        _issues.Visible = _issueList.Count > 0;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the plan shown in the preview. Returns true if the change was made (or there was nothing to do).</summary>
    public async Task<bool> ApplyAsync()
    {
        CommitPendingEdits();
        UpdatePreview();
        if (!IsModified)
        {
            return true;
        }

        if (!CanApply)
        {
            var errors = string.Join("\n", _issueList.Where(i => i.Severity == IssueSeverity.Error).Select(i => "• " + i.Message));
            MessageBox.Show(this, IsReadOnly ? "This object is shown read-only." : "Fix these problems first:\n\n" + errors,
                "Apply", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!ConfirmApply(_plan))
        {
            return false;
        }

        var oldName = OriginalName;
        try
        {
            UseWaitCursor = true;
            await new SchemaChangeService(Session).ApplyAsync(_plan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UseWaitCursor = false;
            Message?.Invoke(this, ($"{Title}: {ex.Message}", true));
            MessageBox.Show(this, ex.Message + "\n\nNothing was changed.", "Apply", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        await OnAppliedAsync();
        UseWaitCursor = false;
        Message?.Invoke(this, ($"{Title}: changes applied.", false));
        Applied?.Invoke(this, new SchemaAppliedEventArgs(ObjectType, oldName, OriginalName ?? string.Empty));
        UpdatePreview();
        return true;
    }

    /// <summary>Before closing: asks to apply or discard changes. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        CommitPendingEdits();
        UpdatePreview();
        if (!IsModified || IsReadOnly)
        {
            return true;
        }

        var answer = MessageBox.Show(this, $"Apply the changes to {Title}?", "SQLite Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => await ApplyAsync(),
            DialogResult.No => true,
            _ => false,
        };
    }

    protected void ReportMessage(string text, bool isError) => Message?.Invoke(this, (text, isError));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewDelay.Dispose();
        }

        base.Dispose(disposing);
    }
}
