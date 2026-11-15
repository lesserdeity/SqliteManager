using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.Documents;

/// <summary>Visual designer to create or change a table: columns, indexes, foreign keys, and options.</summary>
internal sealed class TableDesigner : SchemaEditorBase
{
    private static readonly string[] CommonTypes = ["INTEGER", "TEXT", "REAL", "BLOB", "NUMERIC", "ANY", "INT", "BOOLEAN", "DATE", "DATETIME", "VARCHAR(255)", "DECIMAL(10,2)"];
    private static readonly string[] Collations = ["BINARY", "NOCASE", "RTRIM"];
    private static readonly string[] Actions = [string.Empty, "NO ACTION", "RESTRICT", "SET NULL", "SET DEFAULT", "CASCADE"];

    private readonly TextBox _nameBox;
    private readonly TabControl _tabs;
    private readonly DataGridView _columns;
    private readonly DataGridView _indexes;
    private readonly DataGridView _foreignKeys;
    private readonly CheckBox _withoutRowId;
    private readonly CheckBox _strict;
    private readonly TextBox _constraints;
    private readonly Panel _content;
    private readonly Dictionary<(object Model, string Field), string> _inputErrors = [];
    private readonly Dictionary<IndexDefinition, string> _unreadIndexText = [];
    private string? _tableName;
    private TableDefinition? _original;
    private TableDefinition _design = new();
    private TableDependencies _dependencies = TableDependencies.None;
    private long _rowCount;
    private List<string> _otherNames = [];
    private List<string> _tableNames = [];
    private bool _loading;

    public TableDesigner(DatabaseSession session, string? tableName)
        : base(session)
    {
        _tableName = tableName;

        _nameBox = new TextBox { Width = LogicalToDeviceUnits(260), Margin = new Padding(0, 3, 0, 0) };
        _nameBox.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                _design.Name = _nameBox.Text.Trim();
                RequestPreview();
            }
        };
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 4), WrapContents = false };
        header.Controls.Add(new Label { Text = "Table name:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        header.Controls.Add(_nameBox);

        _columns = CreateGrid();
        _columns.Columns.AddRange(
            TextColumn("Name", 150),
            TextColumn("Type", 120, "Declared type, e.g. INTEGER, TEXT, REAL, BLOB"),
            Check("PK", "Primary key (in the order you tick them)"),
            Check("AI", "AUTOINCREMENT (single INTEGER primary key only)"),
            Check("Not Null"),
            Check("Unique"),
            TextColumn("Default", 120, "A SQL value: 'text', 42, NULL, CURRENT_TIMESTAMP, or (expression)"),
            TextColumn("Check", 160, "CHECK expression, e.g. age >= 0"),
            TextColumn("Collation", 80, "BINARY, NOCASE, or RTRIM"),
            TextColumn("Other", 200, "Other constraints kept as written, e.g. GENERATED ALWAYS AS (a * 2) STORED"));
        _columns.CellValueChanged += OnColumnCellChanged;

        _indexes = CreateGrid();
        var pick = new DataGridViewButtonColumn { HeaderText = string.Empty, Text = "â€¦", UseColumnTextForButtonValue = true, Width = LogicalToDeviceUnits(28), ToolTipText = "Pick columns" };
        _indexes.Columns.AddRange(TextColumn("Name", 180), Check("Unique"), TextColumn("Columns", 240, "Columns, e.g. last_name, first_name DESC"), pick, TextColumn("Where", 220, "Optional condition for a partial index"));
        _indexes.CellValueChanged += OnIndexCellChanged;
        _indexes.CellContentClick += (_, e) =>
        {
            if (e.ColumnIndex == 3 && e.RowIndex >= 0)
            {
                PickIndexColumns(e.RowIndex);
            }
        };

        _foreignKeys = CreateGrid();
        _foreignKeys.Columns.AddRange(
            TextColumn("Columns", 160, "Columns in this table, e.g. customer_id"),
            new DataGridViewComboBoxColumn { HeaderText = "References table", Width = LogicalToDeviceUnits(160), FlatStyle = FlatStyle.Flat },
            TextColumn("Referenced columns", 160, "Leave empty to use the referenced table's primary key"),
            Combo("On update", Actions),
            Combo("On delete", Actions));
        _foreignKeys.CellValueChanged += OnForeignKeyCellChanged;

        _withoutRowId = new CheckBox { Text = "WITHOUT ROWID (needs a primary key; rows are stored in key order)", AutoSize = true };
        _strict = new CheckBox { Text = "STRICT (column types are enforced; types must be INT, INTEGER, REAL, TEXT, BLOB, or ANY)", AutoSize = true };
        _withoutRowId.CheckedChanged += (_, _) => OnOptionChanged(() => _design.WithoutRowId = _withoutRowId.Checked);
        _strict.CheckedChanged += (_, _) => OnOptionChanged(() => _design.Strict = _strict.Checked);
        _constraints = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
        _constraints.TextChanged += (_, _) => OnOptionChanged(() =>
            _design.TableConstraints = _constraints.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList());
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        options.Controls.Add(_withoutRowId);
        options.Controls.Add(_strict);
        options.Controls.Add(new Label { Text = "Table constraints (UNIQUE and CHECK), one per line:", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        options.Controls.Add(_constraints);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(Page("Columns", _columns));
        _tabs.TabPages.Add(Page("Indexes", _indexes));
        _tabs.TabPages.Add(Page("Foreign Keys", _foreignKeys));
        _tabs.TabPages.Add(Page("Options", options));

        _content = new Panel { Dock = DockStyle.Fill };
        _content.Controls.Add(_tabs);
        _content.Controls.Add(header);
        EditorArea.Controls.Add(_content);
    }

    public override string Title => "Design: " + (string.IsNullOrWhiteSpace(_design.Name) ? "(new table)" : _design.Name);

    public override SchemaObjectType ObjectType => SchemaObjectType.Table;

    public override string? OriginalName => _tableName;

    public bool IsNew => _tableName is null;

    /// <summary>Reads the table (or sets up a new one) and fills the grids.</summary>
    public override async Task LoadAsync()
    {
        var objects = await new SchemaService(Session).GetObjectsAsync();
        _tableNames = objects.Where(o => o.Type == SchemaObjectType.Table && !o.IsSystem).Select(o => o.Name).ToList();
        _otherNames = objects.Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.View or SchemaObjectType.Index or SchemaObjectType.Trigger)
            .Select(o => o.Name)
            .Where(n => _tableName is null || !n.Equals(_tableName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_tableName is null)
        {
            var name = "NewTable";
            for (var i = 2; _otherNames.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            {
                name = $"NewTable{i}";
            }

            _original = null;
            _design = new TableDefinition { Name = name, Columns = [new ColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKeyOrder = 1 }] };
        }
        else
        {
            try
            {
                var loaded = await new SchemaChangeService(Session).LoadTableAsync(_tableName);
                _original = loaded.Definition;
                _dependencies = loaded.Dependencies;
                _rowCount = loaded.RowCount;
                _design = _original.Clone();
            }
            catch (SqlParseException ex)
            {
                ShowReadOnly(ex.Message, objects.FirstOrDefault(o => o.Name.Equals(_tableName, StringComparison.OrdinalIgnoreCase))?.Sql);
                UpdatePreview();
                return;
            }
        }

        _inputErrors.Clear();
        PopulateAll();
        UpdatePreview();
    }

    public override void Revert()
    {
        _design = _original?.Clone() ?? _design;
        if (_original is null)
        {
            _design.Columns = [new ColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKeyOrder = 1 }];
            _design.Indexes.Clear();
            _design.ForeignKeys.Clear();
            _design.TableConstraints.Clear();
            _design.WithoutRowId = _design.Strict = false;
        }

        _inputErrors.Clear();
        PopulateAll();
        UpdatePreview();
    }

    protected override async Task OnAppliedAsync()
    {
        _tableName = _design.Name;
        await LoadAsync();
    }

    protected override void CommitPendingEdits()
    {
        _columns.EndEdit();
        _indexes.EndEdit();
        _foreignKeys.EndEdit();
        base.CommitPendingEdits();
    }

    protected override (AlterPlan Plan, IReadOnlyList<DesignIssue> Issues, string Summary) BuildPlan()
    {
        if (IsReadOnly)
        {
            return (AlterPlan.Empty, [], "Read-only: the designer cannot safely read this table's SQL.");
        }

        var issues = DesignValidator.Validate(_design, _original, _rowCount, _otherNames).ToList();
        issues.AddRange(_inputErrors.Values.Select(m => new DesignIssue(IssueSeverity.Error, m)));
        var plan = _original is null ? AlterPlanner.PlanCreate(_design) : AlterPlanner.PlanAlter(_original, _design, _dependencies);
        issues.AddRange((plan.Warnings ?? []).Select(w => new DesignIssue(IssueSeverity.Warning, w)));

        var summary = plan.Kind switch
        {
            AlterKind.Create => "Creates the table.",
            AlterKind.Native => "Uses ALTER TABLE. No data is copied.",
            AlterKind.Rebuild => $"Rebuilds the table: the {_rowCount:N0} row(s) are copied into a new table in one transaction. Reason: {plan.RebuildReason}",
            _ => "No changes.",
        };
        return (plan, issues, summary);
    }

    protected override bool ConfirmApply(AlterPlan plan)
    {
        var lost = plan.DroppedColumns.Select(c => $"â€¢ Column \"{c}\" and its data in {_rowCount:N0} row(s)")
            .Concat(plan.DroppedIndexes.Select(i => $"â€¢ Index \"{i}\""))
            .ToList();
        if (lost.Count == 0)
        {
            return true;
        }

        return MessageBox.Show(this, $"Apply will permanently delete:\n\n{string.Join("\n", lost)}\n\nThis cannot be undone. Continue?",
            "Apply", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    // ---- Row commands (ribbon) -----------------------------------------------------------

    private DataGridView? ActiveGrid => _tabs.SelectedIndex switch
    {
        0 => _columns,
        1 => _indexes,
        2 => _foreignKeys,
        _ => null,
    };

    public bool CanEditRows => !IsReadOnly && ActiveGrid is not null;

    public void AddRow()
    {
        if (!CanEditRows)
        {
            return;
        }

        CommitPendingEdits();
        switch (_tabs.SelectedIndex)
        {
            case 0:
                var name = "column";
                for (var i = 1; _design.FindColumn(name) is not null; i++)
                {
                    name = $"column{i}";
                }

                _design.Columns.Add(new ColumnDefinition { Name = name, Type = "TEXT" });
                PopulateColumns();
                break;
            case 1:
                var indexName = $"ix_{_design.Name}";
                for (var i = 2; _design.Indexes.Any(x => x.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase)) || _otherNames.Contains(indexName, StringComparer.OrdinalIgnoreCase); i++)
                {
                    indexName = $"ix_{_design.Name}_{i}";
                }

                _design.Indexes.Add(new IndexDefinition { Name = indexName });
                PopulateIndexes();
                break;
            case 2:
                _design.ForeignKeys.Add(new ForeignKeyDefinition());
                PopulateForeignKeys();
                break;
        }

        var grid = ActiveGrid!;
        grid.CurrentCell = grid[0, grid.RowCount - 1];
        grid.Focus();
        grid.BeginEdit(true);
        RequestPreview();
    }

    public void RemoveSelectedRows()
    {
        if (!CanEditRows)
        {
            return;
        }

        CommitPendingEdits();
        var grid = ActiveGrid!;
        var rows = grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Append(grid.CurrentCell?.RowIndex ?? -1)
            .Where(r => r >= 0).Distinct().OrderDescending().ToList();
        foreach (var row in rows)
        {
            var model = grid.Rows[row].Tag!;
            switch (model)
            {
                case ColumnDefinition c:
                    _design.Columns.Remove(c);
                    break;
                case IndexDefinition i:
                    _design.Indexes.Remove(i);
                    break;
                case ForeignKeyDefinition f:
                    _design.ForeignKeys.Remove(f);
                    break;
            }

            foreach (var key in _inputErrors.Keys.Where(k => ReferenceEquals(k.Model, model)).ToList())
            {
                _inputErrors.Remove(key);
            }
        }

        PopulateAll();
        RequestPreview();
    }

    public void MoveSelectedRow(int delta)
    {
        if (!CanEditRows || ActiveGrid!.CurrentCell is not { } cell)
        {
            return;
        }

        CommitPendingEdits();
        var from = cell.RowIndex;
        var to = from + delta;
        bool moved = _tabs.SelectedIndex switch
        {
            0 => MoveItem(_design.Columns, from, to),
            1 => MoveItem(_design.Indexes, from, to),
            _ => MoveItem(_design.ForeignKeys, from, to),
        };
        if (!moved)
        {
            return;
        }

        PopulateAll();
        ActiveGrid.CurrentCell = ActiveGrid[cell.ColumnIndex, to];
        RequestPreview();
    }

    private static bool MoveItem<T>(List<T> list, int from, int to)
    {
        if (to < 0 || to >= list.Count)
        {
            return false;
        }

        (list[from], list[to]) = (list[to], list[from]);
        return true;
    }

    // ---- Populating ------------------------------------------------------------------------

    private void PopulateAll()
    {
        _loading = true;
        try
        {
            _nameBox.Text = _design.Name;
            _withoutRowId.Checked = _design.WithoutRowId;
            _strict.Checked = _design.Strict;
            _constraints.Text = string.Join(Environment.NewLine, _design.TableConstraints);
        }
        finally
        {
            _loading = false;
        }

        PopulateColumns();
        PopulateIndexes();
        PopulateForeignKeys();
    }

    private void PopulateColumns()
    {
        _loading = true;
        try
        {
            _columns.Rows.Clear();
            foreach (var c in _design.Columns)
            {
                var row = _columns.Rows[_columns.Rows.Add(c.Name, c.Type, c.IsPrimaryKey, c.AutoIncrement, c.NotNull, c.Unique, c.Default ?? string.Empty, c.Check ?? string.Empty, c.Collation ?? string.Empty, c.Extra ?? string.Empty)];
                row.Tag = c;
                if (c.IsPrimaryKey)
                {
                    row.Cells[2].ToolTipText = $"Primary key column {c.PrimaryKeyOrder}" + (c.PrimaryKeyDescending ? " (DESC)" : string.Empty);
                }

                if (c.OriginalName is not null && !c.OriginalName.Equals(c.Name, StringComparison.Ordinal))
                {
                    row.Cells[0].ToolTipText = $"Renamed from \"{c.OriginalName}\"";
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void PopulateIndexes()
    {
        _loading = true;
        try
        {
            _indexes.Rows.Clear();
            foreach (var index in _design.Indexes)
            {
                // Keep text the user typed that could not be read, so they can fix it.
                var text = _inputErrors.ContainsKey((index, "Columns")) && _unreadIndexText.TryGetValue(index, out var raw)
                    ? raw
                    : TableSqlGenerator.IndexTermsDisplay(index.Terms);
                var row = _indexes.Rows[_indexes.Rows.Add(index.Name, index.Unique, text, string.Empty, index.Where ?? string.Empty)];
                row.Tag = index;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void PopulateForeignKeys()
    {
        _loading = true;
        try
        {
            var tableColumn = (DataGridViewComboBoxColumn)_foreignKeys.Columns[1];
            tableColumn.Items.Clear();
            var names = _tableNames.Append(_design.Name).Concat(_design.ForeignKeys.Select(f => f.Table))
                .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            tableColumn.Items.Add(string.Empty);
            tableColumn.Items.AddRange([.. names]);

            _foreignKeys.Rows.Clear();
            foreach (var fk in _design.ForeignKeys)
            {
                var row = _foreignKeys.Rows[_foreignKeys.Rows.Add(
                    string.Join(", ", fk.Columns),
                    names.FirstOrDefault(n => n.Equals(fk.Table, StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
                    string.Join(", ", fk.ReferencedColumns),
                    NormalizeAction(fk.OnUpdate),
                    NormalizeAction(fk.OnDelete))];
                row.Tag = fk;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static string NormalizeAction(string? action) => Actions.FirstOrDefault(a => a.Equals(action?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    // ---- Edits ---------------------------------------------------------------------------

    private void OnColumnCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || _columns.Rows[e.RowIndex].Tag is not ColumnDefinition column)
        {
            return;
        }

        var value = _columns[e.ColumnIndex, e.RowIndex].Value;
        var text = (value as string)?.Trim() ?? string.Empty;
        var flag = value is true;
        switch (e.ColumnIndex)
        {
            case 0:
                _design.RenameColumn(column, text);
                PopulateIndexes();
                PopulateForeignKeys();
                break;
            case 1:
                column.Type = text;
                break;
            case 2:
                if (flag && !column.IsPrimaryKey)
                {
                    column.PrimaryKeyOrder = _design.Columns.Max(c => c.PrimaryKeyOrder) + 1;
                }
                else if (!flag && column.IsPrimaryKey)
                {
                    column.PrimaryKeyOrder = 0;
                    var order = 1;
                    foreach (var pk in _design.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.PrimaryKeyOrder))
                    {
                        pk.PrimaryKeyOrder = order++;
                    }
                }

                BeginInvoke(PopulateColumns);
                break;
            case 3:
                column.AutoIncrement = flag;
                break;
            case 4:
                column.NotNull = flag;
                break;
            case 5:
                column.Unique = flag;
                break;
            case 6:
                column.Default = NullIfEmpty(text);
                break;
            case 7:
                column.Check = NullIfEmpty(text);
                break;
            case 8:
                column.Collation = NullIfEmpty(text);
                break;
            case 9:
                column.Extra = NullIfEmpty(text);
                break;
        }

        RequestPreview();
    }

    private void OnIndexCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || _indexes.Rows[e.RowIndex].Tag is not IndexDefinition index)
        {
            return;
        }

        var value = _indexes[e.ColumnIndex, e.RowIndex].Value;
        var text = (value as string)?.Trim() ?? string.Empty;
        switch (e.ColumnIndex)
        {
            case 0:
                index.Name = text;
                break;
            case 1:
                index.Unique = value is true;
                break;
            case 2:
                try
                {
                    index.Terms = IndexParser.ParseTermList(text);
                    _inputErrors.Remove((index, "Columns"));
                    _unreadIndexText.Remove(index);
                }
                catch (SqlParseException ex)
                {
                    _inputErrors[(index, "Columns")] = $"Index \"{index.Name}\" columns: {ex.Message}";
                    _unreadIndexText[index] = text;
                }

                break;
            case 4:
                index.Where = NullIfEmpty(text);
                break;
        }

        RequestPreview();
    }

    private void OnForeignKeyCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || _foreignKeys.Rows[e.RowIndex].Tag is not ForeignKeyDefinition fk)
        {
            return;
        }

        var text = (_foreignKeys[e.ColumnIndex, e.RowIndex].Value as string)?.Trim() ?? string.Empty;
        switch (e.ColumnIndex)
        {
            case 0:
                fk.Columns = ParseNames(text, fk, "Columns", "Foreign key columns");
                break;
            case 1:
                fk.Table = text;
                break;
            case 2:
                fk.ReferencedColumns = ParseNames(text, fk, "Referenced", "Foreign key referenced columns");
                break;
            case 3:
                fk.OnUpdate = NullIfEmpty(text);
                break;
            case 4:
                fk.OnDelete = NullIfEmpty(text);
                break;
        }

        RequestPreview();
    }

    /// <summary>Reads "a, \"b c\"" into names; records an input error if the text cannot be read.</summary>
    private List<string> ParseNames(string text, object model, string field, string label)
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
                    throw new SqlParseException("Use column names separated by commas.");
                }
            }

            _inputErrors.Remove((model, field));
            return names;
        }
        catch (SqlParseException ex)
        {
            _inputErrors[(model, field)] = $"{label}: {ex.Message}";
            return [];
        }
    }

    private void OnOptionChanged(Action apply)
    {
        if (_loading)
        {
            return;
        }

        apply();
        RequestPreview();
    }

    private void PickIndexColumns(int rowIndex)
    {
        if (_indexes.Rows[rowIndex].Tag is not IndexDefinition index)
        {
            return;
        }

        _indexes.EndEdit();
        using var dialog = new ColumnPickerDialog(_design.Columns.Select(c => c.Name).ToList(), index.Terms.Where(t => t.Column is not null).Select(t => t.Column!).ToList());
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var previous = index.Terms.Where(t => t.Column is not null).ToDictionary(t => t.Column!, StringComparer.OrdinalIgnoreCase);
        index.Terms = dialog.SelectedColumns.Select(c => previous.GetValueOrDefault(c) ?? IndexTerm.ForColumn(c))
            .Concat(index.Terms.Where(t => t.Column is null))
            .ToList();
        _inputErrors.Remove((index, "Columns"));
        PopulateIndexes();
        RequestPreview();
    }

    private void ShowReadOnly(string reason, string? sql)
    {
        IsReadOnly = true;
        _content.Controls.Clear();
        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10f),
            BackColor = Theme.WindowBack,
            Text = (sql ?? string.Empty).ReplaceLineEndings(Environment.NewLine),
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(48),
            Padding = new Padding(8),
            ForeColor = Theme.ErrorText,
            Text = $"This table is shown read-only because the designer cannot read all of its SQL safely: {reason} " +
                   "Use a query tab to change it, so nothing is lost.",
        };
        _content.Controls.Add(text);
        _content.Controls.Add(label);
        ReportMessage($"{_tableName}: opened read-only. {reason}", true);
    }

    // ---- Grid helpers --------------------------------------------------------------------

    private DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersWidth = LogicalToDeviceUnits(28),
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = Theme.WindowBack,
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            GridColor = Theme.GridLines,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.PaneHeader;
        grid.DefaultCellStyle.SelectionBackColor = Theme.ButtonPressed;
        grid.DefaultCellStyle.SelectionForeColor = Theme.SelectionText;
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit check boxes and drop-downs at once so the preview follows each click.
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox box)
            {
                var header = grid.CurrentCell?.OwningColumn?.HeaderText;
                var source = header switch
                {
                    "Type" => CommonTypes,
                    "Collation" => Collations,
                    "Columns" or "Referenced columns" when grid == _indexes || grid == _foreignKeys => _design.Columns.Select(c => c.Name).ToArray(),
                    _ => null,
                };
                box.AutoCompleteMode = source is null ? AutoCompleteMode.None : AutoCompleteMode.SuggestAppend;
                box.AutoCompleteSource = source is null ? AutoCompleteSource.None : AutoCompleteSource.CustomSource;
                if (source is not null)
                {
                    var list = new AutoCompleteStringCollection();
                    list.AddRange(source);
                    box.AutoCompleteCustomSource = list;
                }
            }
        };
        grid.DataError += (_, e) => e.ThrowException = false;
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode && grid.SelectedRows.Count > 0)
            {
                e.Handled = true;
                RemoveSelectedRows();
            }
        };
        return grid;
    }

    private DataGridViewTextBoxColumn TextColumn(string header, int width, string? tip = null) =>
        new() { HeaderText = header, Width = LogicalToDeviceUnits(width), ToolTipText = tip ?? header, SortMode = DataGridViewColumnSortMode.NotSortable };

    private DataGridViewCheckBoxColumn Check(string header, string? tip = null) =>
        new() { HeaderText = header, Width = LogicalToDeviceUnits(header.Length > 3 ? 64 : 36), ToolTipText = tip ?? header, SortMode = DataGridViewColumnSortMode.NotSortable };

    private DataGridViewComboBoxColumn Combo(string header, string[] items)
    {
        var column = new DataGridViewComboBoxColumn { HeaderText = header, Width = LogicalToDeviceUnits(110), FlatStyle = FlatStyle.Flat };
        column.Items.AddRange([.. items]);
        return column;
    }

    private static TabPage Page(string text, Control content)
    {
        var page = new TabPage(text);
        page.Controls.Add(content);
        return page;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>Lets the user tick columns for an index; the order of ticking is the index order.</summary>
    private sealed class ColumnPickerDialog : Form
    {
        private readonly CheckedListBox _list;
        private readonly List<string> _order;

        public ColumnPickerDialog(IReadOnlyList<string> columns, IReadOnlyList<string> selected)
        {
            Text = "Index Columns";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(LogicalToDeviceUnits(280), LogicalToDeviceUnits(360));
            _order = selected.Where(s => columns.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

            _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var column in columns)
            {
                _list.Items.Add(column, _order.Contains(column, StringComparer.OrdinalIgnoreCase));
            }

            _list.ItemCheck += (_, e) =>
            {
                var name = (string)_list.Items[e.Index];
                _order.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (e.NewValue == CheckState.Checked)
                {
                    _order.Add(name);
                }
            };

            var hint = new Label { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(36), Text = "Tick columns in index order. Add DESC in the grid if needed.", Padding = new Padding(4) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = LogicalToDeviceUnits(80) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = LogicalToDeviceUnits(80) };
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(34), Padding = new Padding(4) };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(buttons);
        }

        public IReadOnlyList<string> SelectedColumns => _order;
    }
}
