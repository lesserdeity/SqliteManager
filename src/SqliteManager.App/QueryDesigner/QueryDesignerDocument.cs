using System.Diagnostics;
using System.Globalization;
using SqliteManager.App.Documents;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.QueryDesign;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.QueryDesigner;

/// <summary>An Access-style query design tab: canvas on top, criteria grid / SQL / results below.</summary>
internal sealed class QueryDesignerDocument : UserControl
{
    private readonly Func<int> _rowLimit;
    private readonly DesignCanvas _canvas;
    private readonly CriteriaGrid _grid;
    private readonly SqlEditor _sqlView;
    private readonly ListBox _problems;
    private readonly TabControl _bottom;
    private readonly TabPage _fieldsPage;
    private readonly TabPage _sqlPage;
    private readonly TabPage _resultsPage;
    private readonly SplitContainer _split;
    private readonly ToolStripDropDownButton _addTable;
    private readonly ToolStripButton _totals;
    private readonly ToolStripButton _distinct;
    private readonly ToolStripTextBox _limit;
    private readonly ToolStripStatusLabel _status;
    private readonly Dictionary<string, TableDetails?> _details = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _tableNames = [];
    private QueryDesignDocument _design = new();
    private CancellationTokenSource? _cts;
    private bool _modified;
    private bool _updatingOptions;

    public QueryDesignerDocument(DatabaseSession session, string untitledName, Func<int> rowLimit)
    {
        Session = session;
        UntitledName = untitledName;
        _rowLimit = rowLimit;

        _canvas = new DesignCanvas { Dock = DockStyle.Fill };
        _canvas.ColumnsProvider = name => _details.TryGetValue(name, out var d) ? d?.Columns : null;
        _canvas.ForeignKeyProvider = name => _details.TryGetValue(name, out var d) && d is not null
            ? d.ForeignKeys.SelectMany(f => f.FromColumns).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        _canvas.Changed += (_, change) =>
        {
            if (change == CanvasChange.Design)
            {
                _grid!.Reload();
                UpdateSql();
            }

            SetModified();
        };
        _canvas.TableDropped += async (_, e) =>
        {
            if (e.Session != Session.Id)
            {
                OnMessage("Drag tables from the database this design uses (" + Session.DisplayName + ").", true);
                return;
            }

            await AddTableAsync(e.Name, e.Location);
        };

        _grid = new CriteriaGrid { Dock = DockStyle.Fill };
        _grid.FieldsChanged += (_, _) =>
        {
            _canvas.Invalidate();
            UpdateSql();
            SetModified();
        };

        _sqlView = new SqlEditor { Dock = DockStyle.Fill, ReadOnly = true };
        _problems = new ListBox
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(70),
            BorderStyle = BorderStyle.None,
            ForeColor = Theme.ErrorText,
            BackColor = Theme.WindowBack,
            IntegralHeight = false,
            Visible = false,
        };

        _fieldsPage = new TabPage("Fields");
        _fieldsPage.Controls.Add(_grid);
        _sqlPage = new TabPage("SQL");
        _sqlPage.Controls.Add(_sqlView);
        _sqlPage.Controls.Add(_problems);
        _resultsPage = new TabPage("Results");
        _bottom = new TabControl { Dock = DockStyle.Fill };
        _bottom.TabPages.AddRange([_fieldsPage, _sqlPage, _resultsPage]);

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.WorkspaceBack };
        _split.Panel1.Controls.Add(_canvas);
        _split.Panel2.Controls.Add(_bottom);

        _addTable = new ToolStripDropDownButton("Add Table", Icons.Glyph(Icons.Add, 16, Theme.GlyphColor)) { ToolTipText = "Add a table or view to the design" };
        _totals = new ToolStripButton("Totals", Icons.Glyph(Icons.Sigma, 16, Theme.GlyphColor)) { CheckOnClick = true, ToolTipText = "Group rows and use Sum, Count, Avg, Min, Max (the Total column)" };
        _totals.CheckedChanged += (_, _) => OnOptionChanged();
        _distinct = new ToolStripButton("Unique Values") { CheckOnClick = true, ToolTipText = "SELECT DISTINCT: leave out duplicate rows" };
        _distinct.CheckedChanged += (_, _) => OnOptionChanged();
        _limit = new ToolStripTextBox { Width = LogicalToDeviceUnits(60), ToolTipText = "Return at most this many rows (empty: all rows)" };
        _limit.TextChanged += (_, _) => OnOptionChanged();
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new RibbonRenderer(), BackColor = Theme.RibbonBody, Padding = new Padding(4, 2, 4, 2) };
        toolbar.Items.AddRange(
        [
            _addTable, new ToolStripSeparator(), _totals, _distinct, new ToolStripLabel("Limit:"), _limit,
        ]);

        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        var footer = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false, BackColor = Theme.RibbonBody, Renderer = new RibbonRenderer() };
        footer.Items.AddRange([new ToolStripStatusLabel(session.DisplayName), new ToolStripSeparator(), _status]);

        Controls.Add(_split);
        Controls.Add(toolbar);
        Controls.Add(footer);
        LoadDesign(new QueryDesignDocument());
    }

    /// <summary>Raised when the title, modified flag, or running state changes.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler<(string Text, bool IsError)>? Message;

    /// <summary>The user wants the generated SQL in a query tab.</summary>
    public event EventHandler<string>? SendToQueryRequested;

    /// <summary>The user wants to export the result of the generated SQL.</summary>
    public event EventHandler<string>? ExportRequested;

    public DatabaseSession Session { get; }

    public string UntitledName { get; }

    public string? FilePath { get; private set; }

    public string Title => FilePath is null ? UntitledName : Path.GetFileName(FilePath);

    public bool IsModified => _modified;

    public bool IsRunning => _cts is not null;

    public GeneratedQuery Generated => QuerySqlGenerator.Generate(_design);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = Math.Max(LogicalToDeviceUnits(120), _split.Height * 55 / 100);
    }

    /// <summary>Reads the table list and the details of the tables in the design.</summary>
    public async Task RefreshSchemaAsync()
    {
        if (!Session.IsOpen)
        {
            return;
        }

        var schema = new SchemaService(Session);
        var objects = await schema.GetObjectsAsync();
        _tableNames = objects.Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.View && !o.IsSystem).Select(o => o.Name).ToList();
        FillAddTableMenu(objects);

        _details.Clear();
        foreach (var name in _design.Tables.Select(t => t.Table).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await LoadDetailsAsync(name);
        }

        var missing = _design.Tables.Where(t => _details.GetValueOrDefault(t.Table) is null).Select(t => t.Table).Distinct().ToList();
        if (missing.Count > 0)
        {
            OnMessage($"Not found in {Session.DisplayName}: {string.Join(", ", missing)}. Remove them from the design or open the right database.", true);
        }

        _canvas.RefreshDesign();
        UpdateSql();
    }

    private void FillAddTableMenu(IReadOnlyList<SchemaObject> objects)
    {
        _addTable.DropDownItems.Clear();
        foreach (var obj in objects.Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.View && !o.IsSystem))
        {
            var name = obj.Name;
            var image = obj.Type == SchemaObjectType.Table ? Icons.Glyph(Icons.Table, 16, Theme.GlyphColor) : Icons.Glyph(Icons.View, 16, Theme.GlyphColor);
            _addTable.DropDownItems.Add(new ToolStripMenuItem(name, image, async (_, _) => await AddTableAsync(name, null)));
        }

        if (_addTable.DropDownItems.Count == 0)
        {
            _addTable.DropDownItems.Add(new ToolStripMenuItem("(no tables)") { Enabled = false });
        }
    }

    /// <summary>Shows the Add Table list (used by the ribbon button).</summary>
    public void ShowAddTableMenu() => _addTable.ShowDropDown();

    private async Task LoadDetailsAsync(string table)
    {
        try
        {
            _details[table] = await new SchemaService(Session).GetTableDetailsAsync(table);
        }
        catch (ArgumentException)
        {
            _details[table] = null; // Not in this database.
        }
    }

    /// <summary>Adds a table to the canvas and draws joins from its foreign keys.</summary>
    public async Task AddTableAsync(string table, Point? location)
    {
        if (!_details.ContainsKey(table))
        {
            await LoadDetailsAsync(table);
        }

        var name = _details[table]?.Name ?? table;
        var at = location ?? _canvas.NextFreeLocation();
        var added = _design.AddTable(_tableNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name, at.X, at.Y);
        var joins = JoinSuggester.Suggest(_design, added, n => _details.GetValueOrDefault(n));
        _design.Joins.AddRange(joins);
        _canvas.RefreshDesign();
        _grid.Reload();
        UpdateSql();
        SetModified();
        if (joins.Count > 0)
        {
            OnMessage($"Added {added.Alias} with {joins.Count} join(s) from foreign keys.", false);
        }
    }

    /// <summary>Replaces the design (new or loaded from a file).</summary>
    public void LoadDesign(QueryDesignDocument design, string? filePath = null)
    {
        _design = design;
        FilePath = filePath;
        _canvas.Design = design;
        _grid.SetDesign(design);
        _updatingOptions = true;
        try
        {
            _totals.Checked = design.Totals;
            _distinct.Checked = design.Distinct;
            _limit.Text = design.Limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            _updatingOptions = false;
        }

        _modified = false;
        UpdateSql();
        OnStateChanged();
    }

    public async Task SaveAsync(string path)
    {
        _design.Database = Session.FilePath;
        await QueryDesignSerializer.SaveAsync(path, _design);
        FilePath = path;
        _modified = false;
        OnStateChanged();
    }

    private void OnOptionChanged()
    {
        if (_updatingOptions)
        {
            return;
        }

        _design.Totals = _totals.Checked;
        _design.Distinct = _distinct.Checked;
        var text = _limit.Text.Trim();
        _design.Limit = text.Length == 0 ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ? limit : -1;
        _grid.Reload();
        UpdateSql();
        SetModified();
    }

    private void UpdateSql()
    {
        var result = QuerySqlGenerator.Generate(_design);
        _sqlView.ReadOnly = false;
        _sqlView.Text = result.Sql ?? "-- The design is not complete. See the problems below.";
        _sqlView.ReadOnly = true;
        _problems.Items.Clear();
        foreach (var error in result.Errors)
        {
            _problems.Items.Add(error);
        }

        _problems.Visible = result.Errors.Count > 0;
        _status.ForeColor = result.Succeeded ? Theme.WindowText : Theme.ErrorText;
        _status.Text = result.Succeeded
            ? $"{_design.Tables.Count} table(s), {_design.Joins.Count} join(s), {_design.Fields.Count(f => !string.IsNullOrWhiteSpace(f.Field))} field(s)"
            : result.Errors[0];
    }

    /// <summary>Runs the generated SQL and shows the rows in the Results tab.</summary>
    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var result = QuerySqlGenerator.Generate(_design);
        if (!result.Succeeded)
        {
            _bottom.SelectedTab = _sqlPage;
            OnMessage("The design cannot run yet: " + result.Errors[0], true);
            return;
        }

        var sql = result.Sql!;
        var limit = _rowLimit();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        _status.Text = "Running...";
        OnStateChanged();
        var watch = Stopwatch.StartNew();
        try
        {
            var outcome = await Session.ExecuteStatementAsync(sql, limit, cts.Token);
            ClearResults();
            if (outcome.Rows is { } rows)
            {
                _resultsPage.Controls.Add(new ResultGrid(rows, outcome.Truncated, limit, () => ExportRequested?.Invoke(this, sql)) { Dock = DockStyle.Fill });
                _status.ForeColor = Theme.WindowText;
                _status.Text = $"{rows.Rows.Count:N0} row(s) in {watch.Elapsed.TotalMilliseconds:N0} ms";
            }

            _bottom.SelectedTab = _resultsPage;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            ClearResults();
            _status.ForeColor = Theme.ErrorText;
            _status.Text = ex.Message;
            OnMessage("Query design: " + ex.Message, true);
        }
        finally
        {
            _cts = null;
            OnStateChanged();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void SendToQuery()
    {
        var result = QuerySqlGenerator.Generate(_design);
        if (!result.Succeeded)
        {
            _bottom.SelectedTab = _sqlPage;
            OnMessage("The design is not complete: " + result.Errors[0], true);
            return;
        }

        SendToQueryRequested?.Invoke(this, result.Sql!);
    }

    private void ClearResults()
    {
        foreach (Control control in _resultsPage.Controls.Cast<Control>().ToList())
        {
            _resultsPage.Controls.Remove(control);
            control.Dispose();
        }
    }

    private void SetModified()
    {
        if (!_modified)
        {
            _modified = true;
            OnStateChanged();
        }
    }

    private void OnMessage(string text, bool isError) => Message?.Invoke(this, (text, isError));

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
        }

        base.Dispose(disposing);
    }
}
