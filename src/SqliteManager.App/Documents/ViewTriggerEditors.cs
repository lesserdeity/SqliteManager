using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.App.Documents;

/// <summary>Creates or changes a view: name, optional column names, and the SELECT.</summary>
internal sealed class ViewEditor : SchemaEditorBase
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _columnsBox = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional, e.g. id, full_name" };
    private readonly SqlEditor _select = new() { Dock = DockStyle.Fill };
    private string? _viewName;
    private string? _originalSql;
    private List<string> _otherNames = [];
    private bool _loading;

    public ViewEditor(DatabaseSession session, string? viewName)
        : base(session)
    {
        _viewName = viewName;
        var layout = EditorLayout.Create(
            ("View name:", _nameBox),
            ("Column names:", _columnsBox));
        layout.Controls.Add(new Label { Text = "SELECT statement:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        layout.SetColumnSpan(_select, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_select, 0, 3);
        EditorArea.Controls.Add(layout);

        _nameBox.TextChanged += (_, _) => Changed();
        _columnsBox.TextChanged += (_, _) => Changed();
        _select.TextChanged += (_, _) => Changed();
    }

    public override string Title => "View: " + (_nameBox.Text.Trim() is { Length: > 0 } n ? n : "(new view)");

    public override SchemaObjectType ObjectType => SchemaObjectType.View;

    public override string? OriginalName => _viewName;

    public override void SetSchema(SchemaSnapshot schema)
    {
        base.SetSchema(schema);
        _select.SetSchema(schema);
    }

    public override async Task LoadAsync()
    {
        var objects = await new SchemaService(Session).GetObjectsAsync();
        _otherNames = objects.Select(o => o.Name).Where(n => _viewName is null || !n.Equals(_viewName, StringComparison.OrdinalIgnoreCase)).ToList();
        _loading = true;
        try
        {
            if (_viewName is null)
            {
                _nameBox.Text = EditorLayout.UniqueName("NewView", _otherNames);
                _columnsBox.Text = string.Empty;
                _select.Text = "SELECT ";
                _originalSql = null;
            }
            else
            {
                var sql = objects.First(o => o.Type == SchemaObjectType.View && o.Name.Equals(_viewName, StringComparison.OrdinalIgnoreCase)).Sql!;
                try
                {
                    var view = ViewDefinition.Parse(sql);
                    _nameBox.Text = _viewName;
                    _columnsBox.Text = string.Join(", ", view.ColumnNames);
                    _select.Text = view.Select;
                    _originalSql = view.ToSql();
                }
                catch (SqlParseException ex)
                {
                    IsReadOnly = true;
                    _select.Text = sql;
                    _select.ReadOnly = true;
                    ReportMessage($"{_viewName}: opened read-only. {ex.Message}", true);
                }
            }

            _select.EmptyUndoBuffer();
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    public override void Revert() => _ = LoadAsync();

    protected override async Task OnAppliedAsync()
    {
        _viewName = _nameBox.Text.Trim();
        await LoadAsync();
    }

    protected override (AlterPlan Plan, IReadOnlyList<DesignIssue> Issues, string Summary) BuildPlan()
    {
        if (IsReadOnly)
        {
            return (AlterPlan.Empty, [], "Read-only: the editor cannot read this view's SQL.");
        }

        var issues = new List<DesignIssue>();
        var name = _nameBox.Text.Trim();
        EditorLayout.CheckName(name, "view", _otherNames, issues);
        if (string.IsNullOrWhiteSpace(_select.Text) || _select.Text.Trim().Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DesignIssue(IssueSeverity.Error, "Enter the SELECT statement."));
        }

        var columns = EditorLayout.ParseNames(_columnsBox.Text, "Column names", issues);
        var sql = new ViewDefinition { Name = name, ColumnNames = columns, Select = _select.Text }.ToSql();
        if (_originalSql is not null && sql == _originalSql)
        {
            return (AlterPlan.Empty, issues, "No changes.");
        }

        var plan = SchemaObjectCommands.PlanReplace(SchemaObjectType.View, _viewName, sql);
        return (plan, issues, _viewName is null ? "Creates the view." : "Drops the view and creates it again with the new definition (views hold no data).");
    }

    private void Changed()
    {
        if (!_loading)
        {
            RequestPreview();
        }
    }
}

/// <summary>Creates or changes a trigger, with fields for each part or as raw SQL.</summary>
internal sealed class TriggerEditor : SchemaEditorBase
{
    private static readonly string[] Timings = ["BEFORE", "AFTER", "INSTEAD OF", "(none)"];
    private static readonly string[] EventNames = ["INSERT", "UPDATE", "DELETE"];

    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _table = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _timing = new() { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _event = new() { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TextBox _columns = new() { Dock = DockStyle.Fill, PlaceholderText = "UPDATE OF columns (optional), e.g. price, qty" };
    private readonly CheckBox _forEachRow = new() { Text = "FOR EACH ROW", AutoSize = true, Checked = true };
    private readonly TextBox _when = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional condition, e.g. NEW.qty > 0" };
    private readonly SqlEditor _body = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _rawMode = new() { Text = "Edit as SQL", AutoSize = true };
    private readonly SqlEditor _raw = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TableLayoutPanel _fields;
    private readonly string? _defaultTable;
    private string? _triggerName;
    private string? _originalSql;
    private List<string> _otherNames = [];
    private bool _loading;

    public TriggerEditor(DatabaseSession session, string? triggerName, string? defaultTable)
        : base(session)
    {
        _triggerName = triggerName;
        _defaultTable = defaultTable;
        _timing.Items.AddRange([.. Timings]);
        _event.Items.AddRange([.. EventNames]);

        var whenRow = new Panel { Dock = DockStyle.Fill, Height = 26 };
        whenRow.Controls.Add(_columns);
        whenRow.Controls.Add(_event);
        whenRow.Controls.Add(_timing);

        _fields = EditorLayout.Create(
            ("Trigger name:", _nameBox),
            ("On table or view:", _table),
            ("When it runs:", whenRow),
            (string.Empty, _forEachRow),
            ("WHEN:", _when));
        _fields.Controls.Add(new Label { Text = "Statements (between BEGIN and END):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 5);
        _fields.SetColumnSpan(_body, 2);
        _fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _fields.Controls.Add(_body, 0, 6);

        var top = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 4, 6, 0) };
        top.Controls.Add(_rawMode);
        EditorArea.Controls.Add(_fields);
        EditorArea.Controls.Add(_raw);
        EditorArea.Controls.Add(top);

        foreach (var control in new Control[] { _nameBox, _table, _columns, _when, _body, _raw })
        {
            control.TextChanged += (_, _) => Changed();
        }

        _timing.SelectedIndexChanged += (_, _) => Changed();
        _event.SelectedIndexChanged += (_, _) =>
        {
            _columns.Enabled = _event.SelectedIndex == 1;
            Changed();
        };
        _forEachRow.CheckedChanged += (_, _) => Changed();
        _rawMode.CheckedChanged += (_, _) => ToggleRawMode();
    }

    public override string Title => "Trigger: " + (CurrentName() is { Length: > 0 } n ? n : "(new trigger)");

    public override SchemaObjectType ObjectType => SchemaObjectType.Trigger;

    public override string? OriginalName => _triggerName;

    public override void SetSchema(SchemaSnapshot schema)
    {
        base.SetSchema(schema);
        _body.SetSchema(schema);
        _raw.SetSchema(schema);
    }

    public override async Task LoadAsync()
    {
        var objects = await new SchemaService(Session).GetObjectsAsync();
        _otherNames = objects.Select(o => o.Name).Where(n => _triggerName is null || !n.Equals(_triggerName, StringComparison.OrdinalIgnoreCase)).ToList();
        _loading = true;
        try
        {
            _table.Items.Clear();
            _table.Items.AddRange([.. objects.Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.View && !o.IsSystem).Select(o => o.Name)]);

            TriggerDefinition trigger;
            if (_triggerName is null)
            {
                trigger = new TriggerDefinition { Name = EditorLayout.UniqueName("NewTrigger", _otherNames), Table = _defaultTable ?? string.Empty, Body = "SELECT 1;" };
                _originalSql = null;
            }
            else
            {
                var sql = objects.First(o => o.Type == SchemaObjectType.Trigger && o.Name.Equals(_triggerName, StringComparison.OrdinalIgnoreCase)).Sql!;
                try
                {
                    trigger = TriggerDefinition.Parse(sql);
                    _originalSql = trigger.ToSql();
                }
                catch (SqlParseException)
                {
                    // Edit the statement as written.
                    _originalSql = sql;
                    _raw.Text = sql;
                    SetRawMode(true);
                    return;
                }
            }

            ShowFields(trigger);
            SetRawMode(false);
        }
        finally
        {
            _loading = false;
            _body.EmptyUndoBuffer();
            _raw.EmptyUndoBuffer();
            UpdatePreview();
        }
    }

    public override void Revert() => _ = LoadAsync();

    protected override async Task OnAppliedAsync()
    {
        _triggerName = CurrentName();
        await LoadAsync();
    }

    protected override (AlterPlan Plan, IReadOnlyList<DesignIssue> Issues, string Summary) BuildPlan()
    {
        var issues = new List<DesignIssue>();
        string sql;
        if (_rawMode.Checked)
        {
            sql = _raw.Text.Trim().TrimEnd(';').TrimEnd();
            if (sql.Length == 0)
            {
                issues.Add(new DesignIssue(IssueSeverity.Error, "Enter the CREATE TRIGGER statement."));
            }
        }
        else
        {
            var trigger = ReadFields(issues);
            EditorLayout.CheckName(trigger.Name, "trigger", _otherNames, issues);
            if (string.IsNullOrWhiteSpace(trigger.Table))
            {
                issues.Add(new DesignIssue(IssueSeverity.Error, "Choose the table or view."));
            }

            if (string.IsNullOrWhiteSpace(trigger.Body))
            {
                issues.Add(new DesignIssue(IssueSeverity.Error, "Enter at least one statement."));
            }

            sql = trigger.ToSql();
        }

        if (_originalSql is not null && sql == _originalSql.Trim().TrimEnd(';').TrimEnd())
        {
            return (AlterPlan.Empty, issues, "No changes.");
        }

        var plan = SchemaObjectCommands.PlanReplace(SchemaObjectType.Trigger, _triggerName, sql);
        return (plan, issues, _triggerName is null ? "Creates the trigger." : "Drops the trigger and creates it again with the new definition.");
    }

    private TriggerDefinition ReadFields(List<DesignIssue> issues) => new()
    {
        Name = _nameBox.Text.Trim(),
        Table = _table.Text.Trim(),
        Timing = _timing.SelectedIndex switch
        {
            0 => TriggerTiming.Before,
            1 => TriggerTiming.After,
            2 => TriggerTiming.InsteadOf,
            _ => TriggerTiming.Unspecified,
        },
        Event = _event.SelectedIndex switch
        {
            1 => TriggerEvent.Update,
            2 => TriggerEvent.Delete,
            _ => TriggerEvent.Insert,
        },
        UpdateColumns = _event.SelectedIndex == 1 ? EditorLayout.ParseNames(_columns.Text, "UPDATE OF columns", issues) : [],
        ForEachRow = _forEachRow.Checked,
        When = string.IsNullOrWhiteSpace(_when.Text) ? null : _when.Text.Trim(),
        Body = _body.Text,
    };

    private void ShowFields(TriggerDefinition trigger)
    {
        _nameBox.Text = trigger.Name;
        _table.Text = trigger.Table;
        _timing.SelectedIndex = trigger.Timing switch
        {
            TriggerTiming.Before => 0,
            TriggerTiming.After => 1,
            TriggerTiming.InsteadOf => 2,
            _ => 3,
        };
        _event.SelectedIndex = (int)trigger.Event switch
        {
            0 => 2,
            1 => 0,
            _ => 1,
        };
        _columns.Text = string.Join(", ", trigger.UpdateColumns);
        _columns.Enabled = trigger.Event == TriggerEvent.Update;
        _forEachRow.Checked = trigger.ForEachRow;
        _when.Text = trigger.When ?? string.Empty;
        _body.Text = trigger.Body;
    }

    private string CurrentName()
    {
        if (!_rawMode.Checked)
        {
            return _nameBox.Text.Trim();
        }

        try
        {
            return TriggerDefinition.Parse(_raw.Text).Name;
        }
        catch (SqlParseException)
        {
            return _triggerName ?? string.Empty;
        }
    }

    private void ToggleRawMode()
    {
        if (_loading)
        {
            return;
        }

        if (_rawMode.Checked)
        {
            _raw.Text = ReadFields([]).ToSql();
            SetRawMode(true);
        }
        else
        {
            try
            {
                ShowFields(TriggerDefinition.Parse(_raw.Text));
                SetRawMode(false);
            }
            catch (SqlParseException ex)
            {
                MessageBox.Show(this, "The SQL cannot be shown as fields: " + ex.Message, "Trigger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetRawMode(true);
            }
        }

        RequestPreview();
    }

    private void SetRawMode(bool raw)
    {
        var loading = _loading;
        _loading = true;
        _rawMode.Checked = raw;
        _loading = loading;
        _raw.Visible = raw;
        _fields.Visible = !raw;
        if (raw)
        {
            _raw.BringToFront();
        }
    }

    private void Changed()
    {
        if (!_loading)
        {
            RequestPreview();
        }
    }
}

/// <summary>Shared layout and checks for the view and trigger editors.</summary>
internal static class EditorLayout
{
    /// <summary>A two-column form: labels on the left, inputs on the right.</summary>
    public static TableLayoutPanel Create(params (string Label, Control Input)[] rows)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = rows[i].Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, i);
            layout.Controls.Add(rows[i].Input, 1, i);
        }

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return layout;
    }

    public static string UniqueName(string baseName, IReadOnlyCollection<string> taken)
    {
        var name = baseName;
        for (var i = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
        {
            name = baseName + i;
        }

        return name;
    }

    public static void CheckName(string name, string kind, IReadOnlyCollection<string> otherNames, List<DesignIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new DesignIssue(IssueSeverity.Error, $"Enter a {kind} name."));
        }
        else if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DesignIssue(IssueSeverity.Error, "Names starting with \"sqlite_\" are reserved."));
        }
        else if (otherNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new DesignIssue(IssueSeverity.Error, $"An object named \"{name}\" already exists."));
        }
    }

    /// <summary>Reads "a, \"b c\"" into names. Adds an issue if the text cannot be read.</summary>
    public static List<string> ParseNames(string text, string label, List<DesignIssue> issues)
    {
        try
        {
            var tokens = SqlTokenizer.Tokenize(text);
            var names = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (i % 2 == 0 && tokens[i].IsName)
                {
                    names.Add(tokens[i].Value);
                }
                else if (i % 2 == 1 && tokens[i].IsSymbol(","))
                {
                    continue;
                }
                else
                {
                    throw new SqlParseException("Use names separated by commas.");
                }
            }

            return names;
        }
        catch (SqlParseException ex)
        {
            issues.Add(new DesignIssue(IssueSeverity.Error, $"{label}: {ex.Message}"));
            return [];
        }
    }
}
