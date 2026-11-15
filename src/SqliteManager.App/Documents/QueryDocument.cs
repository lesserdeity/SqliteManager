using System.Diagnostics;
using System.Globalization;
using System.Text;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.IO;
using SqliteManager.Core.Query;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.App.Documents;

internal enum ExecuteMode
{
    /// <summary>The selection if there is one, otherwise the whole script (F5).</summary>
    All,

    /// <summary>Only the selected text.</summary>
    Selection,

    /// <summary>The statement at the caret (Ctrl+Enter).</summary>
    Current,
}

/// <summary>Raised after a run so the main window can record history and refresh the schema.</summary>
internal sealed class QueryCompletedEventArgs(DatabaseSession session, string script, QueryRunSummary summary) : EventArgs
{
    public DatabaseSession Session { get; } = session;

    public string Script { get; } = script;

    public QueryRunSummary Summary { get; } = summary;
}

/// <summary>A query tab: SQL editor on top, result tabs and messages below.</summary>
internal sealed class QueryDocument : UserControl
{
    private readonly Func<int> _rowLimit;
    private readonly SqlEditor _editor;
    private readonly TabControl _results;
    private readonly TabPage _messagesPage;
    private readonly ListView _messages;
    private readonly ToolStripStatusLabel _databaseLabel;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _elapsedLabel;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();
    private readonly SplitContainer _split;
    private CancellationTokenSource? _cts;
    private Task _runTask = Task.CompletedTask;
    private int _resultCount;

    public QueryDocument(DatabaseSession? session, string untitledName, Func<int> rowLimit)
    {
        Session = session;
        UntitledName = untitledName;
        _rowLimit = rowLimit;

        _editor = new SqlEditor { Dock = DockStyle.Fill };
        _editor.SavePointLeft += (_, _) => OnStateChanged();
        _editor.SavePointReached += (_, _) => OnStateChanged();

        _messages = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5f),
        };
        _messages.Columns.Add("Message", 2000);
        _messages.DoubleClick += (_, _) => GoToSelectedMessageLine();
        _messages.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                GoToSelectedMessageLine();
            }
            else if (e.Control && e.KeyCode == Keys.C && _messages.SelectedItems.Count > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, _messages.SelectedItems.Cast<ListViewItem>().Select(i => i.Text)));
            }
        };

        _messagesPage = new TabPage("Messages");
        _messagesPage.Controls.Add(_messages);
        _results = new TabControl { Dock = DockStyle.Fill, Alignment = TabAlignment.Top };
        _results.TabPages.Add(_messagesPage);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.WorkspaceBack,
        };
        _split.Panel1.Controls.Add(_editor);
        _split.Panel2.Controls.Add(_results);

        _databaseLabel = new ToolStripStatusLabel();
        _statusLabel = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _elapsedLabel = new ToolStripStatusLabel();
        var footer = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false, BackColor = Theme.RibbonBody, Renderer = new RibbonRenderer() };
        footer.Items.AddRange([_databaseLabel, new ToolStripSeparator(), _statusLabel, _elapsedLabel]);

        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _elapsedTimer.Tick += (_, _) => _elapsedLabel.Text = FormatElapsed(_elapsed.Elapsed);

        Controls.Add(_split);
        Controls.Add(footer);
        UpdateDatabaseLabel();
    }

    /// <summary>Raised when the title, modified flag, running state, or database changes.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler<QueryCompletedEventArgs>? QueryCompleted;

    /// <summary>Raised when the user asks to export a result (the database and the statement).</summary>
    public event EventHandler<(DatabaseSession Session, string Sql)>? ExportRequested;

    public DatabaseSession? Session { get; private set; }

    public string UntitledName { get; }

    public string? FilePath { get; private set; }

    public Encoding FileEncoding { get; private set; } = SqlFileService.DefaultEncoding;

    public string Title => FilePath is null ? UntitledName : Path.GetFileName(FilePath);

    public bool IsModified => _editor.Modified;

    public bool IsRunning { get; private set; }

    public bool HasSelection => _editor.SelectionEnd > _editor.SelectionStart;

    public string SqlText => _editor.Text;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = Math.Max(LogicalToDeviceUnits(80), _split.Height * 55 / 100);
        _editor.Focus();
    }

    /// <summary>Connects the tab to another database (or none) and reloads names for autocomplete.</summary>
    public async Task SetSessionAsync(DatabaseSession? session)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot change the database while a query is running.");
        }

        Session = session;
        UpdateDatabaseLabel();
        OnStateChanged();
        await RefreshSchemaAsync();
    }

    /// <summary>Reloads table, column, and function names for autocomplete.</summary>
    public async Task RefreshSchemaAsync()
    {
        var session = Session;
        if (session is null || !session.IsOpen)
        {
            _editor.SetSchema(SchemaSnapshot.Empty);
            return;
        }

        try
        {
            var snapshot = await new SchemaService(session).GetSnapshotAsync();
            if (Session == session && !IsDisposed)
            {
                _editor.SetSchema(snapshot);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddMessage($"Could not read the schema for autocomplete: {ex.Message}", Theme.ErrorText);
        }
    }

    /// <summary>Replaces the text, e.g. after opening a file. The new text counts as saved.</summary>
    public void LoadText(string text, string? filePath, Encoding? encoding)
    {
        _editor.Text = text;
        _editor.EmptyUndoBuffer();
        _editor.SetSavePoint();
        FilePath = filePath;
        FileEncoding = encoding ?? SqlFileService.DefaultEncoding;
        OnStateChanged();
    }

    /// <summary>Inserts text at the caret, replacing the selection.</summary>
    public void InsertText(string text)
    {
        _editor.ReplaceSelection(text);
        _editor.Focus();
    }

    /// <summary>Saves to <paramref name="filePath"/> and remembers it as this tab's file.</summary>
    public async Task SaveAsync(string filePath)
    {
        await SqlFileService.WriteAsync(filePath, _editor.Text, FileEncoding);
        FilePath = filePath;
        _editor.SetSavePoint();
        OnStateChanged();
    }

    public void FocusEditor() => _editor.Focus();

    public Task WaitUntilIdleAsync() => _runTask;

    public void Cancel()
    {
        if (IsRunning && _cts is { IsCancellationRequested: false })
        {
            _statusLabel.Text = "Cancelling...";
            _cts.Cancel();
        }
    }

    public Task ExecuteAsync(ExecuteMode mode)
    {
        if (IsRunning)
        {
            return _runTask;
        }

        _runTask = ExecuteCoreAsync(mode);
        return _runTask;
    }

    private async Task ExecuteCoreAsync(ExecuteMode mode)
    {
        var session = Session;
        if (session is null || !session.IsOpen)
        {
            ShowMessagesOnly("Choose a database for this query tab first (Query tab > Database).", Theme.ErrorText);
            return;
        }

        string script;
        int start;
        switch (mode)
        {
            case ExecuteMode.Selection when !HasSelection:
                ShowMessagesOnly("Select the SQL to run first.", Theme.ErrorText);
                return;
            case ExecuteMode.All when !HasSelection:
                script = _editor.Text;
                start = 0;
                break;
            case ExecuteMode.All:
            case ExecuteMode.Selection:
                start = _editor.SelectionStart;
                script = _editor.SelectedText;
                break;
            default:
                var statement = SqlScriptSplitter.FindAt(_editor.Text, _editor.CurrentPosition);
                if (statement is null)
                {
                    ShowMessagesOnly("There is no statement at the cursor.", Theme.ErrorText);
                    return;
                }

                script = statement.Text;
                start = statement.StartOffset;
                break;
        }

        var firstLine = _editor.LineFromPosition(start) + 1;
        var rowLimit = _rowLimit();
        ClearResults();

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        _statusLabel.Text = "Executing...";
        _statusLabel.ForeColor = SystemColors.ControlText;
        _elapsed.Restart();
        _elapsedTimer.Start();
        OnStateChanged();

        QueryRunSummary? summary = null;
        try
        {
            var progress = new Progress<StatementResult>(r => AddResult(r, rowLimit));
            summary = await new QueryExecutor(session).ExecuteAsync(script, new QueryRunOptions(rowLimit, firstLine), progress, cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddMessage("Error: " + ex.Message, Theme.ErrorText);
        }
        finally
        {
            _elapsed.Stop();
            _elapsedTimer.Stop();
            _elapsedLabel.Text = FormatElapsed(_elapsed.Elapsed);
            IsRunning = false;
            _cts = null;
        }

        if (summary is not null)
        {
            FinishRun(summary);
            if (summary.ChangedSchema)
            {
                await RefreshSchemaAsync();
            }

            QueryCompleted?.Invoke(this, new QueryCompletedEventArgs(session, script, summary));
        }

        OnStateChanged();
    }

    /// <summary>
    /// Shows EXPLAIN QUERY PLAN as a tree for the selection, or else the statement at the caret (Ctrl+L).
    /// Full table scans are shown in a warning color.
    /// </summary>
    public async Task ExplainAsync()
    {
        var session = Session;
        if (IsRunning)
        {
            return;
        }

        if (session is null || !session.IsOpen)
        {
            ShowMessagesOnly("Choose a database for this query tab first (Query tab > Database).", Theme.ErrorText);
            return;
        }

        string text;
        int start;
        if (HasSelection)
        {
            text = _editor.SelectedText;
            start = _editor.SelectionStart;
        }
        else if (SqlScriptSplitter.FindAt(_editor.Text, _editor.CurrentPosition) is { } statement)
        {
            text = statement.Text;
            start = statement.StartOffset;
        }
        else
        {
            ShowMessagesOnly("There is no statement at the cursor.", Theme.ErrorText);
            return;
        }

        ClearResults();
        try
        {
            var (explained, plan) = await QueryPlan.ExplainAsync(session, text);
            var tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ShowNodeToolTips = true,
                Font = new Font("Consolas", 9.5f),
                BackColor = Theme.WindowBack,
                ForeColor = Theme.WindowText,
                ItemHeight = LogicalToDeviceUnits(22),
            };
            var scans = 0;
            void Add(TreeNodeCollection parent, QueryPlanNode node)
            {
                var item = parent.Add(node.Detail);
                if (node.IsFullScan)
                {
                    scans++;
                    item.ForeColor = Theme.ErrorText;
                    item.ToolTipText = "Reads every row of the table. An index on the columns in WHERE or JOIN may make this faster.";
                }

                node.Children.ForEach(c => Add(item.Nodes, c));
            }

            foreach (var node in plan)
            {
                Add(tree.Nodes, node);
            }

            tree.ExpandAll();
            var page = new TabPage("Query Plan") { ToolTipText = OneLine(explained.Text) };
            page.Controls.Add(tree);
            _results.TabPages.Insert(0, page);
            _results.SelectedTab = page;
            var line = _editor.LineFromPosition(start) + explained.StartLine;
            AddMessage($"Query plan for the statement on line {line}: {plan.Count} top-level step(s)" + (scans > 0 ? $", {scans} full table scan(s)." : "."),
                scans > 0 ? Theme.ErrorText : SystemColors.WindowText, line);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidOperationException)
        {
            AddMessage("Error: " + ex.Message, Theme.ErrorText);
            _results.SelectedTab = _messagesPage;
        }
    }

    private void AddResult(StatementResult result, int rowLimit)
    {
        var prefix = $"Statement {result.Index} (line {result.Line}): ";
        var time = $"{result.Duration.TotalMilliseconds.ToString("0.#", CultureInfo.CurrentCulture)} ms";

        if (result.Cancelled)
        {
            AddMessage(prefix + "cancelled.", Theme.ErrorText, result.Line);
        }
        else if (result.Error is not null)
        {
            AddMessage($"Line {result.ErrorLine ?? result.Line}: {result.Error}", Theme.ErrorText, result.ErrorLine ?? result.Line);
        }
        else if (result.Rows is { } rows)
        {
            _resultCount++;
            var page = new TabPage($"Result {_resultCount}") { ToolTipText = OneLine(result.Statement.Text) };
            // Only read-only statements are offered for export, because the export runs the statement again.
            var session = Session;
            var statementSql = result.Statement.Text;
            Action? export = session is not null && result.Statement.FirstKeyword is "SELECT" or "WITH" or "VALUES"
                ? () => ExportRequested?.Invoke(this, (session, statementSql))
                : null;
            page.Controls.Add(new ResultGrid(rows, result.Truncated, rowLimit, export) { Dock = DockStyle.Fill });
            _results.TabPages.Insert(_results.TabPages.Count - 1, page);
            if (_resultCount == 1)
            {
                _results.SelectedTab = page;
            }

            var text = $"{prefix}{rows.Rows.Count:N0} row(s) returned in {time}";
            if (result.Truncated)
            {
                text += $". Stopped at the row limit of {rowLimit:N0}.";
            }

            AddMessage(text, result.Truncated ? Theme.ErrorText : SystemColors.WindowText, result.Line);
        }
        else if (result.RowsAffected >= 0)
        {
            AddMessage($"{prefix}{result.RowsAffected:N0} row(s) affected in {time}", SystemColors.WindowText, result.Line);
        }
        else
        {
            AddMessage($"{prefix}completed in {time}", SystemColors.WindowText, result.Line);
        }
    }

    private void FinishRun(QueryRunSummary summary)
    {
        var total = FormatElapsed(summary.Duration);
        if (summary.Cancelled)
        {
            _statusLabel.Text = "Query cancelled.";
            _statusLabel.ForeColor = Theme.ErrorText;
        }
        else if (summary.Failed)
        {
            _statusLabel.Text = "Query completed with errors.";
            _statusLabel.ForeColor = Theme.ErrorText;
        }
        else if (summary.StatementCount == 0)
        {
            _statusLabel.Text = "Nothing to run.";
        }
        else
        {
            _statusLabel.Text = "Query executed successfully.";
        }

        var ran = summary.Results.Count(r => r.Succeeded);
        AddMessage($"{ran} of {summary.StatementCount} statement(s) ran in {total}.", SystemColors.GrayText);

        if (summary.Failed || summary.Cancelled || _resultCount == 0)
        {
            _results.SelectedTab = _messagesPage;
        }
    }

    private void ClearResults()
    {
        _resultCount = 0;
        foreach (var page in _results.TabPages.Cast<TabPage>().Where(p => p != _messagesPage).ToList())
        {
            _results.TabPages.Remove(page);
            page.Dispose();
        }

        _messages.Items.Clear();
    }

    private void ShowMessagesOnly(string message, Color color)
    {
        ClearResults();
        AddMessage(message, color);
        _results.SelectedTab = _messagesPage;
    }

    private void AddMessage(string text, Color color, int? line = null)
    {
        var item = _messages.Items.Add(text);
        item.ForeColor = color;
        item.Tag = line;
        item.EnsureVisible();
    }

    private void GoToSelectedMessageLine()
    {
        if (_messages.SelectedItems.Count == 1 && _messages.SelectedItems[0].Tag is int line)
        {
            _editor.GoToLine(line);
        }
    }

    private void UpdateDatabaseLabel()
    {
        _databaseLabel.Text = Session is null ? "No database" : Session.DisplayName + (Session.IsReadOnly ? " (read-only)" : string.Empty);
        _databaseLabel.ToolTipText = Session?.FilePath ?? string.Empty;
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? elapsed.TotalSeconds.ToString("0.000 's'", CultureInfo.CurrentCulture)
            : elapsed.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture);

    private static string OneLine(string sql)
    {
        var text = string.Join(" ", sql.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _elapsedTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
