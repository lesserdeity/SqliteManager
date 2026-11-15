using SqliteManager.App.Documents;
using SqliteManager.App.Panes;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Design;
using SqliteManager.Core.IO;
using SqliteManager.Core.Logging;
using SqliteManager.Core.Query;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Settings;

namespace SqliteManager.App;

/// <summary>Main window: ribbon, object explorer, document tabs, and output panes.</summary>
internal sealed class MainForm : Form
{
    private const string DatabaseFilter =
        "SQLite databases (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|All files (*.*)|*.*";

    private const string SqlFilter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";

    private const string DesignFilter = "Query designs (*.qdesign)|*.qdesign|All files (*.*)|*.*";

    private readonly SqlLog _log = new();
    private readonly DatabaseManager _databases;
    private readonly Transfer.JobsService _jobsService;
    private readonly SettingsStore _settingsStore;
    private readonly QueryHistoryStore _history = new(AppPaths.QueryHistoryFile);
    private readonly AppSettings _settings;
    private readonly string? _settingsError;
    private ToolStripMenuItem _recentMenu = null!;
    private readonly List<ToolStripItem> _needsWritableSession = [];
    private readonly List<ToolStripItem> _needsAnySession = [];
    private ToolStripDropDownButton _detachButton = null!;
    private int _untitledCounter;

    private readonly Ribbon _ribbon;
    private readonly ObjectExplorer _explorer;
    private readonly ClosableTabControl _documents;
    private readonly TabControl _outputTabs;
    private readonly MessagesView _messages;
    private readonly SqlLogView _sqlLog;
    private readonly HistoryView _historyView;
    private readonly SplitContainer _mainSplit;
    private readonly SplitContainer _workSplit;
    private readonly ToolStripStatusLabel _statusText;
    private readonly ToolStripStatusLabel _statusDatabases;

    private readonly List<ToolStripItem> _needsDatabase = [];
    private readonly List<ToolStripItem> _needsDataObject = [];
    private readonly List<ToolStripItem> _needsDocument = [];
    private readonly List<ToolStripItem> _needsQueryIdle = [];
    private readonly List<ToolStripItem> _needsQuery = [];
    private RibbonTab _queryTab = null!;
    private RibbonTab _dataTab = null!;
    private RibbonTab _designTab = null!;
    private RibbonTab _queryDesignTab = null!;
    private readonly List<ToolStripItem> _needsQueryDesign = [];
    private ToolStripButton _runDesignButton = null!;
    private ToolStripButton _cancelDesignButton = null!;
    private int _untitledDesignCounter;
    private ToolStripButton _applyButton = null!;
    private ToolStripButton _revertDesignButton = null!;
    private readonly List<ToolStripItem> _designRowButtons = [];
    private readonly List<ToolStripItem> _needsWritableDatabase = [];
    private ToolStripButton _writeButton = null!;
    private ToolStripButton _revertButton = null!;
    private ToolStripButton _clearFiltersButton = null!;
    private ToolStripButton _blobButton = null!;
    private readonly List<ToolStripItem> _needsEditableData = [];
    private readonly List<ToolStripItem> _needsData = [];
    private ToolStripButton _explorerToggle = null!;
    private ToolStripButton _outputToggle = null!;
    private ToolStripButton _cancelButton = null!;
    private ToolStripComboBox _databasePicker = null!;
    private bool _updatingPicker;

    private bool _closing;

    public MainForm(SettingsStore settingsStore, AppSettings settings, string? settingsError)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _settingsError = settingsError;
        _databases = new DatabaseManager(_log);
        _jobsService = new Transfer.JobsService(_log);
        _jobsService.JobRan += (_, path) => BeginInvoke(async () => await OnDatabaseDataChangedAsync(path));

        Text = "SQLite Manager";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.WindowsDefaultBounds;
        MinimumSize = new Size(800, 500);
        BackColor = Theme.WorkspaceBack;
        KeyPreview = true;

        _explorer = new ObjectExplorer { Dock = DockStyle.Fill };
        _explorer.OpenDataRequested += async (_, _) => await OpenSelectedDataAsync();
        _explorer.NewQueryRequested += async (_, _) => await NewQueryAsync();
        _explorer.RefreshRequested += async (_, _) => await RefreshSelectedAsync();
        _explorer.CloseRequested += async (_, _) => await CloseSelectedDatabaseAsync();
        _explorer.SelectionChanged += (_, _) => UpdateCommandState();
        _explorer.ErrorOccurred += (_, ex) => ShowError("Could not read the schema", ex, showDialog: false);
        _explorer.CommandRequested += async (_, e) => await OnExplorerCommandAsync(e);

        _documents = new ClosableTabControl { Dock = DockStyle.Fill };
        _documents.SelectedIndexChanged += (_, _) => OnActiveDocumentChanged();
        _documents.MouseUp += OnDocumentsMouseUp;
        _documents.CloseRequested += async (_, page) => await CloseDocumentAsync(page);

        _messages = new MessagesView { Dock = DockStyle.Fill };
        _sqlLog = new SqlLogView(_log) { Dock = DockStyle.Fill };
        _historyView = new HistoryView { Dock = DockStyle.Fill };
        _historyView.OpenRequested += async (_, entry) => await OpenHistoryEntryAsync(entry);
        _outputTabs = new TabControl { Dock = DockStyle.Fill, Alignment = TabAlignment.Bottom };
        _outputTabs.TabPages.Add(new TabPage("Messages") { Controls = { _messages } });
        _outputTabs.TabPages.Add(new TabPage("SQL Log") { Controls = { _sqlLog } });
        _outputTabs.TabPages.Add(new TabPage("Query History") { Controls = { _historyView } });

        _workSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            BackColor = Theme.WorkspaceBack,
        };
        _workSplit.Panel1.Controls.Add(_documents);
        _workSplit.Panel1.BackColor = Theme.WorkspaceBack;
        _workSplit.Panel2.Controls.Add(_outputTabs);

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            BackColor = Theme.WorkspaceBack,
        };
        _mainSplit.Panel1.Controls.Add(_explorer);
        _mainSplit.Panel2.Controls.Add(_workSplit);

        _statusText = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.AccentText };
        _statusDatabases = new ToolStripStatusLabel { ForeColor = Theme.AccentText };
        var status = new StatusStrip { Renderer = new RibbonRenderer(), BackColor = Theme.Accent, SizingGrip = true };
        status.Items.AddRange([_statusText, _statusDatabases]);

        _ribbon = BuildRibbon();

        Controls.Add(_mainSplit);
        Controls.Add(_ribbon);
        Controls.Add(status);

        ApplyWindowBounds();
        UpdateCommandState();
    }

    private QueryDocument? ActiveQuery => _documents.SelectedTab?.Tag as QueryDocument;

    private TableDataView? ActiveData => _documents.SelectedTab?.Tag as TableDataView;

    private SchemaEditorBase? ActiveEditor => _documents.SelectedTab?.Tag as SchemaEditorBase;

    private TableDesigner? ActiveDesigner => _documents.SelectedTab?.Tag as TableDesigner;

    private QueryDesigner.QueryDesignerDocument? ActiveQueryDesign => _documents.SelectedTab?.Tag as QueryDesigner.QueryDesignerDocument;

    private IEnumerable<QueryDocument> QueryDocuments => _documents.TabPages.Cast<TabPage>().Select(p => p.Tag).OfType<QueryDocument>();

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyPaneLayout();
        _messages.Info("Ready. Use File > Open or New Database to start.");
        if (_settingsError is not null)
        {
            _messages.Error($"Could not read settings ({_settingsStore.FilePath}): {_settingsError}");
        }

        try
        {
            _historyView.SetEntries(await _history.LoadAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not read settings or query history", ex, showDialog: false);
        }

        try
        {
            await _jobsService.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError($"Could not read the saved jobs ({_jobsService.Store.FilePath})", ex, showDialog: false);
        }
    }

    private Ribbon BuildRibbon()
    {
        var ribbon = new Ribbon();

        var fileMenu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        var fileClose = new ToolStripMenuItem("Close Database", null, async (_, _) => await CloseSelectedDatabaseAsync());
        var fileSave = new ToolStripMenuItem("Save", Glyph(Icons.Save), async (_, _) => await SaveActiveQueryAsync(saveAs: false)) { ShortcutKeyDisplayString = "Ctrl+S" };
        var fileSaveAs = new ToolStripMenuItem("Save As...", Glyph(Icons.SaveAs), async (_, _) => await SaveActiveQueryAsync(saveAs: true));
        _needsDatabase.Add(fileClose);
        _needsQuery.Add(fileSave);
        _needsQuery.Add(fileSaveAs);
        fileMenu.Items.AddRange(
        [
            new ToolStripMenuItem("New Database...", Glyph(Icons.NewDocument), async (_, _) => await NewDatabaseAsync()),
            new ToolStripMenuItem("Open...", Glyph(Icons.OpenFile), async (_, _) => await OpenDatabaseAsync(readOnly: false)),
            new ToolStripMenuItem("Open Read-Only...", Glyph(Icons.Lock), async (_, _) => await OpenDatabaseAsync(readOnly: true)),
            _recentMenu = new ToolStripMenuItem("Recent Files", Glyph(Icons.History)),
            fileClose,
            new ToolStripSeparator(),
            new ToolStripMenuItem("New Query", Glyph(Icons.NewQuery), async (_, _) => await NewQueryAsync()) { ShortcutKeyDisplayString = "Ctrl+N" },
            new ToolStripMenuItem("Open SQL File...", Glyph(Icons.OpenFile), async (_, _) => await OpenSqlFilesAsync()),
            new ToolStripMenuItem("New Query Design", Glyph(Icons.QueryDesign), async (_, _) => await NewQueryDesignAsync(null, null)),
            new ToolStripMenuItem("Open Query Design...", Glyph(Icons.OpenFile), async (_, _) => await OpenQueryDesignsAsync()),
            fileSave,
            fileSaveAs,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Options...", Glyph(Icons.Settings), async (_, _) => await ShowOptionsAsync()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", Glyph(Icons.Exit), (_, _) => Close()),
        ]);
        ribbon.FileMenu = fileMenu;
        _recentMenu.DropDownItems.Add("(none)");
        _recentMenu.DropDownOpening += (_, _) => FillRecentMenu();
        fileMenu.Opening += (_, _) => _recentMenu.Enabled = _settings.RecentFiles.Count > 0;
        ((ToolStripMenuItem)fileMenu.Items[1]).ShortcutKeyDisplayString = "Ctrl+O";

        // Home
        var home = new RibbonTab("Home");
        var database = home.AddGroup("Database");
        database.Items.Add(RibbonButtons.Large("New\nDatabase", Icons.NewDocument, async (_, _) => await NewDatabaseAsync()));
        database.Items.Add(RibbonButtons.Large("Open", Icons.OpenFile, async (_, _) => await OpenDatabaseAsync(readOnly: false)));
        database.Items.Add(RibbonButtons.Large("Open\nRead-Only", Icons.Lock, async (_, _) => await OpenDatabaseAsync(readOnly: true)));
        var close = RibbonButtons.Large("Close\nDatabase", Icons.Close, async (_, _) => await CloseSelectedDatabaseAsync());
        database.Items.Add(close);
        _needsDatabase.Add(close);

        var query = home.AddGroup("Query");
        query.Items.Add(RibbonButtons.Large("New\nQuery", Icons.NewQuery, async (_, _) => await NewQueryAsync()));
        var newDesign = RibbonButtons.Large("Query\nDesign", Icons.QueryDesign, async (_, _) => await NewQueryDesignAsync(null, null));
        newDesign.ToolTipText = "Build a query by dragging tables onto a canvas (Access style)";
        query.Items.Add(newDesign);

        var create = home.AddGroup("Create");
        var newTable = RibbonButtons.Large("New\nTable", Icons.Table, async (_, _) => await OpenNewEditorAsync(ExplorerCommand.NewTable));
        var newView = RibbonButtons.Large("New\nView", Icons.View, async (_, _) => await OpenNewEditorAsync(ExplorerCommand.NewView));
        var newTrigger = RibbonButtons.Large("New\nTrigger", Icons.Trigger, async (_, _) => await OpenNewEditorAsync(ExplorerCommand.NewTrigger));
        create.Items.AddRange([newTable, newView, newTrigger]);
        _needsWritableDatabase.AddRange([newTable, newView, newTrigger]);

        var objects = home.AddGroup("Objects");
        var openData = RibbonButtons.Large("Open\nData", Icons.Grid, async (_, _) => await OpenSelectedDataAsync());
        var refresh = RibbonButtons.Large("Refresh", Icons.Refresh, async (_, _) => await RefreshSelectedAsync());
        objects.Items.Add(openData);
        objects.Items.Add(refresh);
        _needsDataObject.Add(openData);
        _needsDatabase.Add(refresh);

        // Query
        _queryTab = new RibbonTab("Query");
        var execute = _queryTab.AddGroup("Execute");
        var runAll = RibbonButtons.Large("Execute\n(F5)", Icons.Play, async (_, _) => await ExecuteActiveAsync(ExecuteMode.All));
        var runSelection = RibbonButtons.Large("Execute\nSelection", Icons.PlaySelection, async (_, _) => await ExecuteActiveAsync(ExecuteMode.Selection));
        var runCurrent = RibbonButtons.Large("Execute\nCurrent", Icons.PlayCurrent, async (_, _) => await ExecuteActiveAsync(ExecuteMode.Current));
        runAll.ToolTipText = "Run the whole script, or the selection if there is one (F5)";
        runCurrent.ToolTipText = "Run the statement at the cursor (Ctrl+Enter)";
        _cancelButton = RibbonButtons.Large("Cancel", Icons.Stop, (_, _) => ActiveQuery?.Cancel());
        var explain = RibbonButtons.Large("Explain\nPlan", Icons.Explain, async (_, _) => await (ActiveQuery?.ExplainAsync() ?? Task.CompletedTask));
        explain.ToolTipText = "Show EXPLAIN QUERY PLAN as a tree for the statement at the cursor (Ctrl+L)";
        execute.Items.AddRange([runAll, runSelection, runCurrent, _cancelButton, explain]);
        _needsQueryIdle.AddRange([runAll, runSelection, runCurrent, explain]);

        var connection = _queryTab.AddGroup("Database");
        _databasePicker = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Width = LogicalToDeviceUnits(200),
            ToolTipText = "The database this query tab runs against",
        };
        _databasePicker.SelectedIndexChanged += async (_, _) => await OnDatabasePickedAsync();
        connection.Items.Add(_databasePicker);
        _needsQueryIdle.Add(_databasePicker);

        var files = _queryTab.AddGroup("File");
        files.Items.Add(RibbonButtons.Large("Open\nSQL File", Icons.OpenFile, async (_, _) => await OpenSqlFilesAsync()));
        var save = RibbonButtons.Large("Save", Icons.Save, async (_, _) => await SaveActiveQueryAsync(saveAs: false));
        var saveAs = RibbonButtons.Large("Save\nAs", Icons.SaveAs, async (_, _) => await SaveActiveQueryAsync(saveAs: true));
        files.Items.AddRange([save, saveAs]);
        _needsQuery.AddRange([save, saveAs]);

        // Data (table data tabs)
        _dataTab = new RibbonTab("Data");
        var changes = _dataTab.AddGroup("Changes");
        _writeButton = RibbonButtons.Large("Write\nChanges", Icons.Save, async (_, _) => await (ActiveData?.WriteChangesAsync() ?? Task.FromResult(true)));
        _writeButton.ToolTipText = "Write all pending changes in one transaction (Ctrl+S)";
        _revertButton = RibbonButtons.Large("Revert\nChanges", Icons.Undo, (_, _) => ActiveData?.RevertAll());
        changes.Items.AddRange([_writeButton, _revertButton]);

        var rows = _dataTab.AddGroup("Rows");
        var newRow = RibbonButtons.Large("New\nRow", Icons.Add, (_, _) => ActiveData?.AddRow());
        var deleteRows = RibbonButtons.Large("Delete\nRows", Icons.Delete, async (_, _) => await (ActiveData?.DeleteSelectedRowsAsync() ?? Task.CompletedTask));
        deleteRows.ToolTipText = "Mark the selected rows for deletion (Del)";
        var setNull = RibbonButtons.Large("Set to\nNULL", Icons.Remove, async (_, _) => await (ActiveData?.SetSelectionToNullAsync() ?? Task.CompletedTask));
        setNull.ToolTipText = "Set the selected cells to NULL (Alt+Del)";
        _blobButton = RibbonButtons.Large("BLOB\nViewer", Icons.Photo, (_, _) => ActiveData?.ShowBlobViewerForCurrentCell());
        rows.Items.AddRange([newRow, deleteRows, setNull, _blobButton]);
        _needsEditableData.AddRange([newRow, deleteRows, setNull]);

        var dataView = _dataTab.AddGroup("View");
        _clearFiltersButton = RibbonButtons.Large("Clear\nFilters", Icons.Filter, async (_, _) => await (ActiveData?.ClearFiltersAsync() ?? Task.CompletedTask));
        var reload = RibbonButtons.Large("Refresh", Icons.Refresh, async (_, _) => await (ActiveData?.ReloadAsync() ?? Task.CompletedTask));
        reload.ToolTipText = "Read the data again (pending changes are kept)";
        dataView.Items.AddRange([_clearFiltersButton, reload]);
        _needsData.Add(reload);

        // View
        var view = new RibbonTab("View");
        var panes = view.AddGroup("Panes");
        _explorerToggle = RibbonButtons.Large("Object\nExplorer", Icons.Sidebar, (_, _) => _mainSplit.Panel1Collapsed = !_explorerToggle.Checked, checkOnClick: true);
        _explorerToggle.Checked = true;
        _outputToggle = RibbonButtons.Large("Output\nPane", Icons.Panel, (_, _) => _workSplit.Panel2Collapsed = !_outputToggle.Checked, checkOnClick: true);
        _outputToggle.Checked = true;
        panes.Items.Add(_explorerToggle);
        panes.Items.Add(_outputToggle);

        var log = view.AddGroup("Output");
        log.Items.Add(RibbonButtons.Large("SQL\nLog", Icons.List, (_, _) => ShowOutput(_sqlLog)));
        log.Items.Add(RibbonButtons.Large("Query\nHistory", Icons.History, (_, _) => ShowOutput(_historyView)));
        log.Items.Add(RibbonButtons.Large("Clear\nSQL Log", Icons.Clear, (_, _) => _log.Clear()));

        var docs = view.AddGroup("Documents");
        var closeTab = RibbonButtons.Large("Close\nTab", Icons.Close, async (_, _) => await CloseDocumentAsync(_documents.SelectedTab));
        var closeAll = RibbonButtons.Large("Close All\nTabs", Icons.CloseAll, async (_, _) => await CloseAllDocumentsAsync());
        closeTab.ToolTipText = "Close Tab (Ctrl+W or Ctrl+F4)";
        docs.Items.Add(closeTab);
        docs.Items.Add(closeAll);
        _needsDocument.Add(closeTab);
        _needsDocument.Add(closeAll);

        // Design (table designer, view and trigger editors)
        _designTab = new RibbonTab("Design");
        var designChanges = _designTab.AddGroup("Changes");
        _applyButton = RibbonButtons.Large("Apply", Icons.Save, async (_, _) => await ApplyActiveEditorAsync());
        _applyButton.ToolTipText = "Run the SQL shown in the preview (Ctrl+S)";
        _revertDesignButton = RibbonButtons.Large("Revert", Icons.Undo, (_, _) => ActiveEditor?.Revert());
        designChanges.Items.AddRange([_applyButton, _revertDesignButton]);
        var designRows = _designTab.AddGroup("Rows");
        var addRow = RibbonButtons.Large("Add", Icons.Add, (_, _) => ActiveDesigner?.AddRow());
        var removeRow = RibbonButtons.Large("Remove", Icons.Delete, (_, _) => ActiveDesigner?.RemoveSelectedRows());
        var moveUp = RibbonButtons.Large("Move\nUp", Icons.Up, (_, _) => ActiveDesigner?.MoveSelectedRow(-1));
        var moveDown = RibbonButtons.Large("Move\nDown", Icons.Down, (_, _) => ActiveDesigner?.MoveSelectedRow(1));
        addRow.ToolTipText = "Add a column, index, or foreign key (on the selected tab)";
        designRows.Items.AddRange([addRow, removeRow, moveUp, moveDown]);
        _designRowButtons.AddRange([addRow, removeRow, moveUp, moveDown]);

        // Query Design
        _queryDesignTab = new RibbonTab("Query Design");
        var designRun = _queryDesignTab.AddGroup("Results");
        _runDesignButton = RibbonButtons.Large("Run\n(F5)", Icons.Play, async (_, _) => await (ActiveQueryDesign?.RunAsync() ?? Task.CompletedTask));
        _cancelDesignButton = RibbonButtons.Large("Cancel", Icons.Stop, (_, _) => ActiveQueryDesign?.Cancel());
        var sendToQuery = RibbonButtons.Large("Send to\nQuery Tab", Icons.Send, (_, _) => ActiveQueryDesign?.SendToQuery());
        sendToQuery.ToolTipText = "Open the generated SQL in a new query tab, where you can edit it";
        designRun.Items.AddRange([_runDesignButton, _cancelDesignButton, sendToQuery]);
        var designTables = _queryDesignTab.AddGroup("Design");
        var addDesignTable = RibbonButtons.Large("Add\nTable", Icons.Table, (_, _) => ActiveQueryDesign?.ShowAddTableMenu());
        designTables.Items.Add(addDesignTable);
        var designFile = _queryDesignTab.AddGroup("File");
        var openDesign = RibbonButtons.Large("Open\nDesign", Icons.OpenFile, async (_, _) => await OpenQueryDesignsAsync());
        var saveDesign = RibbonButtons.Large("Save", Icons.Save, async (_, _) => await SaveActiveQueryDesignAsync(saveAs: false));
        var saveDesignAs = RibbonButtons.Large("Save\nAs", Icons.SaveAs, async (_, _) => await SaveActiveQueryDesignAsync(saveAs: true));
        designFile.Items.AddRange([openDesign, saveDesign, saveDesignAs]);
        _needsQueryDesign.AddRange([sendToQuery, addDesignTable, saveDesign, saveDesignAs]);

        // Database Tools
        var tools = new RibbonTab("Database Tools");
        var maintenance = tools.AddGroup("Maintenance");
        var vacuum = RibbonButtons.Large("Vacuum", Icons.Compact, async (_, _) => await VacuumAsync());
        vacuum.ToolTipText = "Rebuild the file to free unused space (VACUUM)";
        var analyze = RibbonButtons.Large("Analyze", Icons.Analyze, async (_, _) => await AnalyzeAsync());
        analyze.ToolTipText = "Gather statistics that help the query planner (ANALYZE)";
        var integrity = RibbonButtons.Large("Integrity\nCheck", Icons.CheckMark, async (_, _) => await CheckAsync(foreignKeys: false));
        integrity.ToolTipText = "Check the file for damage (PRAGMA integrity_check)";
        var fkCheck = RibbonButtons.Large("Foreign Key\nCheck", Icons.Link, async (_, _) => await CheckAsync(foreignKeys: true));
        fkCheck.ToolTipText = "List rows that break a foreign key (PRAGMA foreign_key_check)";
        maintenance.Items.AddRange([vacuum, analyze, integrity, fkCheck]);
        var settingsGroup = tools.AddGroup("Settings");
        var pragmas = RibbonButtons.Large("PRAGMA\nSettings", Icons.Tune, async (_, _) => await ShowPragmaEditorAsync());
        pragmas.ToolTipText = "Journal mode, foreign keys, synchronous, cache size, page size, user version";
        settingsGroup.Items.Add(pragmas);
        var backupGroup = tools.AddGroup("Backup");
        var backup = RibbonButtons.Large("Back Up", Icons.Backup, async (_, _) => await BackupAsync());
        backup.ToolTipText = "Copy the database to a new file while it is open (online backup)";
        backupGroup.Items.Add(backup);
        var attachGroup = tools.AddGroup("Attached Databases");
        var attach = RibbonButtons.Large("Attach", Icons.Attach, async (_, _) => await AttachAsync());
        attach.ToolTipText = "ATTACH another database file, so queries can use name.table across files";
        _detachButton = new ToolStripDropDownButton("Detach")
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            ImageScaling = ToolStripItemImageScaling.SizeToFit,
            Image = Icons.Glyph(Icons.Detach, 64, Theme.GlyphColor, "D"),
            Padding = new Padding(4, 2, 4, 2),
            ToolTipText = "DETACH an attached database",
        };
        _detachButton.DropDownOpening += async (_, _) => await FillDetachMenuAsync();
        _detachButton.DropDownItems.Add("(none)");
        attachGroup.Items.AddRange([attach, _detachButton]);
        _needsAnySession.AddRange([integrity, fkCheck, pragmas, backup, attach, _detachButton]);
        _needsWritableSession.AddRange([vacuum, analyze]);

        // External Data
        var external = new RibbonTab("External Data");
        var transfer = external.AddGroup("Import and Export");
        var import = RibbonButtons.Large("Import", Icons.Import, (_, _) => OpenImportWizard(DefaultSession()?.FilePath, _explorer.SelectedObject is { Type: SchemaObjectType.Table } t ? t.Name : null));
        import.ToolTipText = "Import CSV, TSV, Excel, or JSON into a table";
        var export = RibbonButtons.Large("Export", Icons.Export, (_, _) => OpenExportWizard(DefaultSession()?.FilePath, _explorer.SelectedDataObject?.Name, null));
        export.ToolTipText = "Export a table or a query to CSV, TSV, Excel, JSON, or SQL";
        transfer.Items.AddRange([import, export]);
        var jobsGroup = external.AddGroup("Saved Jobs");
        jobsGroup.Items.Add(RibbonButtons.Large("Jobs", Icons.Jobs, async (_, _) => await OpenJobsAsync()));

        ribbon.AddTab(home);
        ribbon.AddTab(_queryTab);
        ribbon.AddTab(_dataTab);
        ribbon.AddTab(_designTab);
        ribbon.AddTab(_queryDesignTab);
        ribbon.AddTab(external);
        ribbon.AddTab(tools);
        ribbon.AddTab(view);
        return ribbon;
    }

    private static Bitmap Glyph(char glyph) => Icons.Glyph(glyph, 16, Theme.GlyphColor);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5 when ActiveQuery is not null:
                _ = ExecuteActiveAsync(ExecuteMode.All);
                return true;
            case Keys.F5 when ActiveQueryDesign is { IsRunning: false } design:
                _ = design.RunAsync();
                return true;
            case Keys.Control | Keys.S when ActiveQueryDesign is not null:
                _ = SaveActiveQueryDesignAsync(saveAs: false);
                return true;
            case Keys.Control | Keys.Enter when ActiveQuery is not null:
                _ = ExecuteActiveAsync(ExecuteMode.Current);
                return true;
            case Keys.Control | Keys.N:
                _ = NewQueryAsync();
                return true;
            case Keys.Control | Keys.O:
                _ = OpenDatabaseAsync(readOnly: false);
                return true;
            case Keys.Control | Keys.L when ActiveQuery is { IsRunning: false } explainQuery:
                _ = explainQuery.ExplainAsync();
                return true;
            case Keys.Control | Keys.S when ActiveQuery is not null:
                _ = SaveActiveQueryAsync(saveAs: false);
                return true;
            case Keys.Control | Keys.S when ActiveEditor is not null:
                _ = ApplyActiveEditorAsync();
                return true;
            case Keys.Control | Keys.S when ActiveData is not null:
                _ = ActiveData.WriteChangesAsync();
                return true;
            case Keys.Control | Keys.W when _documents.SelectedTab is not null:
            case Keys.Control | Keys.F4 when _documents.SelectedTab is not null:
                _ = CloseDocumentAsync(_documents.SelectedTab);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void UpdateCommandState()
    {
        var hasDatabase = _explorer.SelectedSession is not null;
        _needsAnySession.ForEach(i => i.Enabled = DefaultSession() is not null);
        _needsWritableSession.ForEach(i => i.Enabled = DefaultSession() is { IsReadOnly: false });
        var hasDataObject = _explorer.CanOpenData;
        var hasDocument = _documents.TabCount > 0;

        // An empty tab control paints a system background that ignores the theme; show the workspace instead.
        _documents.Visible = hasDocument;
        var query = ActiveQuery;
        _needsDatabase.ForEach(i => i.Enabled = hasDatabase);
        _needsDataObject.ForEach(i => i.Enabled = hasDataObject);
        _needsDocument.ForEach(i => i.Enabled = hasDocument);
        _needsQuery.ForEach(i => i.Enabled = query is not null);
        _needsQueryIdle.ForEach(i => i.Enabled = query is { IsRunning: false });
        _cancelButton.Enabled = query is { IsRunning: true };

        var data = ActiveData;
        _needsData.ForEach(i => i.Enabled = data is not null);
        _needsEditableData.ForEach(i => i.Enabled = data is { CanEdit: true });
        _writeButton.Enabled = data is { HasPendingChanges: true };
        _revertButton.Enabled = data is { HasPendingChanges: true };
        _clearFiltersButton.Enabled = data is { HasFilters: true };
        _blobButton.Enabled = data is { HasCurrentCell: true };

        var editor = ActiveEditor;
        _applyButton.Enabled = editor is { IsModified: true, IsReadOnly: false };
        _revertDesignButton.Enabled = editor is { IsModified: true, IsReadOnly: false };
        _designRowButtons.ForEach(b => b.Enabled = ActiveDesigner is { CanEditRows: true });
        var queryDesign = ActiveQueryDesign;
        _needsQueryDesign.ForEach(b => b.Enabled = queryDesign is not null);
        _runDesignButton.Enabled = queryDesign is { IsRunning: false };
        _cancelDesignButton.Enabled = queryDesign is { IsRunning: true };

        var writable = DefaultSession() is { IsReadOnly: false };
        _needsWritableDatabase.ForEach(b => b.Enabled = writable);

        var count = _databases.Sessions.Count;
        _statusDatabases.Text = count == 1 ? "1 database open" : $"{count} databases open";
        UpdateDatabasePicker();
    }

    /// <summary>Fills the Query > Database list with the open databases and selects the active tab's one.</summary>
    private void UpdateDatabasePicker()
    {
        _updatingPicker = true;
        try
        {
            var sessions = _databases.Sessions;
            _databasePicker.Items.Clear();
            foreach (var session in sessions)
            {
                _databasePicker.Items.Add(new SessionItem(session));
            }

            var current = ActiveQuery?.Session;
            _databasePicker.SelectedIndex = current is null ? -1 : sessions.ToList().IndexOf(current);
        }
        finally
        {
            _updatingPicker = false;
        }
    }

    private async Task OnDatabasePickedAsync()
    {
        if (_updatingPicker || ActiveQuery is not { IsRunning: false } query || _databasePicker.SelectedItem is not SessionItem item)
        {
            return;
        }

        if (query.Session != item.Session)
        {
            await query.SetSessionAsync(item.Session);
        }
    }

    private void OnActiveDocumentChanged()
    {
        if (ActiveQuery is not null)
        {
            _ribbon.SelectTab(_queryTab);
        }
        else if (ActiveData is not null)
        {
            _ribbon.SelectTab(_dataTab);
        }
        else if (ActiveEditor is not null)
        {
            _ribbon.SelectTab(_designTab);
        }
        else if (ActiveQueryDesign is not null)
        {
            _ribbon.SelectTab(_queryDesignTab);
        }

        UpdateCommandState();
    }

    // ---- Databases ----------------------------------------------------------------------------

    private async Task NewDatabaseAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "New Database",
            Filter = DatabaseFilter,
            DefaultExt = "db",
            AddExtension = true,
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (File.Exists(dialog.FileName))
        {
            MessageBox.Show(this,
                $"The file '{dialog.FileName}' already exists.\n\nChoose a different name, or use Open to open the existing file.",
                "New Database", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunAsync($"Creating {Path.GetFileName(dialog.FileName)}...", async () =>
        {
            var session = await _databases.CreateAsync(dialog.FileName);
            await _explorer.AddDatabaseAsync(session);
            _messages.Info($"Created database {session.FilePath}");
            await RememberRecentAsync(session.FilePath, readOnly: false);
        });
    }

    private async Task OpenDatabaseAsync(bool readOnly)
    {
        using var dialog = new OpenFileDialog
        {
            Title = readOnly ? "Open Database (Read-Only)" : "Open Database",
            Filter = DatabaseFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            await OpenFileAsync(file, readOnly);
        }
    }

    private async Task OpenFileAsync(string file, bool readOnly)
    {
        if (_databases.IsOpen(file))
        {
            _messages.Info($"{file} is already open.");
            return;
        }

        await RunAsync($"Opening {Path.GetFileName(file)}...", async () =>
        {
            var session = await _databases.OpenAsync(file, readOnly);
            await _explorer.AddDatabaseAsync(session);
            _messages.Info($"Opened {session.FilePath}{(readOnly ? " (read-only)" : string.Empty)}");
            await RememberRecentAsync(session.FilePath, readOnly);

            // Query tabs without a database connect to the first one opened.
            foreach (var doc in QueryDocuments.Where(d => d.Session is null && !d.IsRunning))
            {
                await doc.SetSessionAsync(session);
            }
        });
    }

    private void FillRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        var number = 1;
        foreach (var recent in _settings.RecentFiles)
        {
            var file = recent;
            var label = $"&{number % 10} {file.Path}{(file.ReadOnly ? "  (read-only)" : string.Empty)}";
            number++;
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, async (_, _) =>
            {
                if (!File.Exists(file.Path))
                {
                    if (MessageBox.Show(this, $"The file '{file.Path}' no longer exists.\n\nRemove it from the recent list?", "Recent Files",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        _settings.RemoveRecentFile(file.Path);
                        await SaveSettingsAsync();
                    }

                    return;
                }

                await OpenFileAsync(file.Path, file.ReadOnly);
            }));
        }

        if (_recentMenu.DropDownItems.Count > 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripSeparator());
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("Clear List", null, async (_, _) =>
            {
                _settings.RecentFiles.Clear();
                await SaveSettingsAsync();
            }));
        }
    }

    private async Task RememberRecentAsync(string path, bool readOnly)
    {
        _settings.AddRecentFile(path, readOnly);
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save settings", ex, showDialog: false);
        }
    }

    private async Task CloseSelectedDatabaseAsync()
    {
        var session = _explorer.SelectedSession;
        if (session is null)
        {
            return;
        }

        // Stop queries running on this database. Query tabs stay open without a database so no text is lost.
        var queries = QueryDocuments.Where(d => d.Session == session).ToList();
        foreach (var doc in queries.Where(d => d.IsRunning))
        {
            doc.Cancel();
            await doc.WaitUntilIdleAsync();
        }

        foreach (var page in _documents.TabPages.Cast<TabPage>()
                     .Where(p => (p.Tag is TableDataView v && v.Session == session) || (p.Tag is SchemaEditorBase s && s.Session == session)
                                 || (p.Tag is QueryDesigner.QueryDesignerDocument d && d.Session == session)).ToList())
        {
            if (!await CloseDocumentAsync(page))
            {
                return; // The user kept a tab with unwritten changes open.
            }
        }

        _explorer.RemoveDatabase(session);
        await RunAsync($"Closing {session.DisplayName}...", async () =>
        {
            await _databases.CloseAsync(session);
            foreach (var doc in queries)
            {
                await doc.SetSessionAsync(null);
            }

            _messages.Info($"Closed {session.FilePath}");
        });
    }

    private async Task RefreshSelectedAsync()
    {
        var session = _explorer.SelectedSession;
        if (session is null)
        {
            return;
        }

        await RunAsync($"Refreshing {session.DisplayName}...", async () =>
        {
            await _explorer.RefreshDatabaseAsync(session);
            foreach (var doc in QueryDocuments.Where(d => d.Session == session))
            {
                await doc.RefreshSchemaAsync();
            }
        });
    }

    private Task OpenSelectedDataAsync()
    {
        var session = _explorer.SelectedSession;
        var obj = _explorer.SelectedDataObject;
        return session is null || obj is null ? Task.CompletedTask : OpenDataAsync(session, obj.Name);
    }

    private async Task OpenDataAsync(DatabaseSession session, string objectName)
    {
        var existing = _documents.TabPages.Cast<TabPage>()
            .FirstOrDefault(p => p.Tag is TableDataView v && v.Session == session && v.ObjectName == objectName);
        if (existing is not null)
        {
            _documents.SelectedTab = existing;
            return;
        }

        var view = new TableDataView(session, objectName) { Dock = DockStyle.Fill };
        var page = new TabPage($"{objectName}  [{session.DisplayName}]") { Tag = view, ToolTipText = session.FilePath };
        page.Controls.Add(view);
        view.StateChanged += (_, _) =>
        {
            var text = $"{objectName}{(view.HasPendingChanges ? "*" : string.Empty)}  [{session.DisplayName}]";
            if (page.Text != text)
            {
                page.Text = text;
            }

            if (ActiveData == view)
            {
                UpdateCommandState();
            }
        };
        view.Message += (_, m) =>
        {
            if (m.IsError)
            {
                _messages.Error(m.Text);
            }
            else
            {
                _messages.Info(m.Text);
            }
        };
        _documents.TabPages.Add(page);
        _documents.SelectedTab = page;
        _ribbon.SelectTab(_dataTab);
        UpdateCommandState();

        await RunAsync($"Loading {objectName}...", async () =>
        {
            try
            {
                await view.LoadAsync();
            }
            catch
            {
                await CloseDocumentAsync(page);
                throw;
            }
        });
    }

    // ---- Schema objects: designer, editors, script, rename, drop -------------------------------

    private async Task OnExplorerCommandAsync(ExplorerCommandEventArgs e)
    {
        var (session, obj) = (e.Session, e.Object);
        switch (e.Command)
        {
            case ExplorerCommand.Design when obj is not null:
                await OpenDesignAsync(session, obj);
                break;
            case ExplorerCommand.NewTable:
                await OpenEditorAsync(new TableDesigner(session, null));
                break;
            case ExplorerCommand.NewView:
                await OpenEditorAsync(new ViewEditor(session, null));
                break;
            case ExplorerCommand.NewTrigger:
                await OpenEditorAsync(new TriggerEditor(session, null, obj?.Type == SchemaObjectType.Table ? obj.Name : null));
                break;
            case ExplorerCommand.ScriptCreate or ExplorerCommand.ScriptInsert or ExplorerCommand.ScriptDrop when obj is not null:
                await ScriptAsync(session, obj, e.Command);
                break;
            case ExplorerCommand.Rename when obj is not null && e.NewName is not null:
                await RenameObjectAsync(session, obj, e.NewName);
                break;
            case ExplorerCommand.Drop when obj is not null:
                await DropObjectAsync(session, obj);
                break;
            case ExplorerCommand.ImportData:
                OpenImportWizard(session.FilePath, obj?.Type == SchemaObjectType.Table ? obj.Name : null);
                break;
            case ExplorerCommand.ExportData:
                OpenExportWizard(session.FilePath, obj?.Type is SchemaObjectType.Table or SchemaObjectType.View ? obj.Name : null, null);
                break;
            case ExplorerCommand.Detach when e.NewName is not null:
                await DetachAsync(session, e.NewName);
                break;
            case ExplorerCommand.NewQueryDesign:
                await NewQueryDesignAsync(session, obj?.Type is SchemaObjectType.Table or SchemaObjectType.View ? obj.Name : null);
                break;
        }
    }

    // ---- Import, export, and jobs --------------------------------------------------------------

    private void OpenImportWizard(string? databasePath, string? table)
    {
        using var wizard = new Transfer.ImportWizard(_jobsService, _log, _databases.Sessions, databasePath: databasePath, table: table);
        wizard.Imported += (_, path) => BeginInvoke(async () => await OnDatabaseDataChangedAsync(path));
        wizard.ShowDialog(this);
    }

    private void OpenExportWizard(string? databasePath, string? table, string? sql)
    {
        using var wizard = new Transfer.ExportWizard(_jobsService, _log, _databases.Sessions, databasePath: databasePath, table: table, sql: sql);
        wizard.ShowDialog(this);
    }

    private async Task OpenJobsAsync()
    {
        var existing = _documents.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Tag is Transfer.JobsDocument);
        if (existing is not null)
        {
            _documents.SelectedTab = existing;
            return;
        }

        void Report(string message, bool isError) => BeginInvoke(() =>
        {
            if (isError)
            {
                _messages.Error(message);
            }
            else
            {
                _messages.Info(message);
            }
        });

        var jobs = new Transfer.JobsDocument(_jobsService, () => _databases.Sessions, Report) { Dock = DockStyle.Fill };
        var page = new TabPage("Jobs") { Tag = jobs, ToolTipText = _jobsService.Store.FilePath };
        page.Controls.Add(jobs);
        _documents.TabPages.Add(page);
        _documents.SelectedTab = page;
        UpdateCommandState();
        await jobs.ReloadAsync();
    }

    /// <summary>After an import or a job changed a database: refresh it in the explorer and reload its data tabs (unless they have unwritten edits).</summary>
    private async Task OnDatabaseDataChangedAsync(string databasePath)
    {
        var session = _databases.Sessions.FirstOrDefault(s => s.FilePath.Equals(databasePath, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            return;
        }

        await OnSchemaChangedAsync(session);
        foreach (var view in _documents.TabPages.Cast<TabPage>().Select(p => p.Tag).OfType<TableDataView>().Where(v => v.Session == session && !v.HasPendingChanges))
        {
            await view.ReloadAsync();
        }
    }

    private Task OpenNewEditorAsync(ExplorerCommand command)
    {
        if (DefaultSession() is not { } session)
        {
            MessageBox.Show(this, "Open or create a database first.", "SQLite Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Task.CompletedTask;
        }

        return OnExplorerCommandAsync(new ExplorerCommandEventArgs(command, session, null, null));
    }

    /// <summary>Opens the designer for a table (or the table of an index), or the editor for a view or trigger.</summary>
    private async Task OpenDesignAsync(DatabaseSession session, SchemaObject obj)
    {
        var (type, name) = obj.Type == SchemaObjectType.Index ? (SchemaObjectType.Table, obj.TableName) : (obj.Type, obj.Name);
        var existing = _documents.TabPages.Cast<TabPage>().FirstOrDefault(p =>
            p.Tag is SchemaEditorBase s && s.Session == session && s.ObjectType == type && string.Equals(s.OriginalName, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _documents.SelectedTab = existing;
            return;
        }

        await OpenEditorAsync(type switch
        {
            SchemaObjectType.Table => new TableDesigner(session, name),
            SchemaObjectType.View => new ViewEditor(session, name),
            _ => new TriggerEditor(session, name, null),
        });
    }

    private async Task OpenEditorAsync(SchemaEditorBase editor)
    {
        editor.Dock = DockStyle.Fill;
        var page = new TabPage { Tag = editor, ToolTipText = editor.Session.FilePath };
        page.Controls.Add(editor);
        void UpdateText()
        {
            var text = $"{editor.Title}{(editor.IsModified && !editor.IsReadOnly ? "*" : string.Empty)}  [{editor.Session.DisplayName}]";
            if (page.Text != text)
            {
                page.Text = text;
            }
        }

        editor.StateChanged += (_, _) =>
        {
            UpdateText();
            if (ActiveEditor == editor)
            {
                UpdateCommandState();
            }
        };
        editor.Message += (_, m) =>
        {
            if (m.IsError)
            {
                _messages.Error(m.Text);
            }
            else
            {
                _messages.Info(m.Text);
            }
        };
        editor.Applied += async (_, e) => await OnEditorAppliedAsync(editor.Session, e);

        UpdateText();
        _documents.TabPages.Add(page);
        _documents.SelectedTab = page;
        _ribbon.SelectTab(_designTab);
        UpdateCommandState();

        await RunAsync($"Loading {editor.Title}...", async () =>
        {
            try
            {
                await editor.LoadAsync();
                editor.SetSchema(await new SchemaService(editor.Session).GetSnapshotAsync());
            }
            catch
            {
                _documents.TabPages.Remove(page);
                page.Dispose();
                throw;
            }
        });
    }

    private async Task ApplyActiveEditorAsync()
    {
        if (ActiveEditor is not { } editor)
        {
            return;
        }

        if (editor is TableDesigner { OriginalName: { } table } && !EnsureNoPendingData(editor.Session, table))
        {
            return;
        }

        await editor.ApplyAsync();
        UpdateCommandState();
    }

    /// <summary>False (with a message) if a data tab of the object has unwritten changes.</summary>
    private bool EnsureNoPendingData(DatabaseSession session, string objectName)
    {
        var pending = _documents.TabPages.Cast<TabPage>().Select(p => p.Tag).OfType<TableDataView>()
            .FirstOrDefault(v => v.Session == session && v.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) && v.HasPendingChanges);
        if (pending is null)
        {
            return true;
        }

        MessageBox.Show(this, $"The data tab for {objectName} has changes that are not written yet. Write or revert them first.",
            "SQLite Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private async Task OnEditorAppliedAsync(DatabaseSession session, SchemaAppliedEventArgs e)
    {
        await OnSchemaChangedAsync(session);
        if (e.OldName is not null && e.Type is SchemaObjectType.Table or SchemaObjectType.View)
        {
            await ReopenDataTabsAsync(session, e.OldName, e.NewName);
        }
    }

    /// <summary>Refreshes the explorer and the autocomplete names of the database's query tabs and editors.</summary>
    private async Task OnSchemaChangedAsync(DatabaseSession session)
    {
        if (!session.IsOpen)
        {
            return;
        }

        await _explorer.RefreshDatabaseAsync(session);
        foreach (var doc in QueryDocuments.Where(d => d.Session == session && !d.IsRunning))
        {
            await doc.RefreshSchemaAsync();
        }

        foreach (var design in _documents.TabPages.Cast<TabPage>().Select(p => p.Tag).OfType<QueryDesigner.QueryDesignerDocument>().Where(d => d.Session == session).ToList())
        {
            await design.RefreshSchemaAsync();
        }

        var editors = _documents.TabPages.Cast<TabPage>().Select(p => p.Tag).OfType<SchemaEditorBase>().Where(x => x.Session == session).ToList();
        if (editors.Count > 0)
        {
            var snapshot = await new SchemaService(session).GetSnapshotAsync();
            editors.ForEach(x => x.SetSchema(snapshot));
        }
    }

    /// <summary>Closes data tabs that show the old structure and opens them again (under the new name if renamed).</summary>
    private async Task ReopenDataTabsAsync(DatabaseSession session, string oldName, string? newName)
    {
        var pages = _documents.TabPages.Cast<TabPage>()
            .Where(p => p.Tag is TableDataView v && v.Session == session && v.ObjectName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var current = _documents.SelectedTab;
        foreach (var page in pages)
        {
            _documents.TabPages.Remove(page);
            page.Dispose();
            if (newName is not null)
            {
                await OpenDataAsync(session, newName);
            }
        }

        if (current is not null && _documents.TabPages.Contains(current))
        {
            _documents.SelectedTab = current;
        }
    }

    private async Task ScriptAsync(DatabaseSession session, SchemaObject obj, ExplorerCommand command)
    {
        await RunAsync($"Scripting {obj.Name}...", async () =>
        {
            var schema = new SchemaService(session);
            var text = command switch
            {
                ExplorerCommand.ScriptCreate => SchemaObjectCommands.ScriptCreate(obj, await schema.GetObjectsAsync()),
                ExplorerCommand.ScriptInsert => SchemaObjectCommands.ScriptInsert(await schema.GetTableDetailsAsync(obj.Name)),
                _ => SchemaObjectCommands.ScriptDrop(obj),
            };
            var doc = await NewQueryAsync(session, text);
            doc.FocusEditor();
        });
    }

    private async Task RenameObjectAsync(DatabaseSession session, SchemaObject obj, string newName)
    {
        if ((obj.Type is SchemaObjectType.Table or SchemaObjectType.View && !EnsureNoPendingData(session, obj.Name)) || !EnsureNoUnappliedEditor(session, obj))
        {
            return;
        }

        await RunAsync($"Renaming {obj.Name}...", async () =>
        {
            await new SchemaChangeService(session).ApplyAsync(SchemaObjectCommands.PlanRename(obj, newName));
            _messages.Info($"Renamed {obj.Name} to {newName}.");
            await OnSchemaChangedAsync(session);
            if (obj.Type is SchemaObjectType.Table or SchemaObjectType.View)
            {
                await ReopenDataTabsAsync(session, obj.Name, newName);
            }

            await RefreshEditorsAfterChangeAsync(session, obj, newName);
        });
    }

    /// <summary>Editors open on an object (or, for an index, on its table).</summary>
    private List<TabPage> EditorPagesFor(DatabaseSession session, SchemaObject obj)
    {
        var (type, name) = obj.Type == SchemaObjectType.Index ? (SchemaObjectType.Table, obj.TableName) : (obj.Type, obj.Name);
        return _documents.TabPages.Cast<TabPage>()
            .Where(p => p.Tag is SchemaEditorBase s && s.Session == session && s.ObjectType == type && string.Equals(s.OriginalName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>False (with a message) if an editor of the object has changes that are not applied.</summary>
    private bool EnsureNoUnappliedEditor(DatabaseSession session, SchemaObject obj)
    {
        if (!EditorPagesFor(session, obj).Any(p => p.Tag is SchemaEditorBase { IsModified: true, IsReadOnly: false }))
        {
            return true;
        }

        MessageBox.Show(this, $"An editor for {obj.Name} has changes that are not applied. Apply or revert them first.",
            "SQLite Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    /// <summary>
    /// After a rename or drop: editors of a renamed object reopen under the new name; the table's designer
    /// reloads when one of its indexes changed. Dropped objects' editors are closed by the caller.
    /// </summary>
    private async Task RefreshEditorsAfterChangeAsync(DatabaseSession session, SchemaObject obj, string? newName)
    {
        foreach (var page in EditorPagesFor(session, obj))
        {
            var editor = (SchemaEditorBase)page.Tag!;
            if (obj.Type == SchemaObjectType.Index)
            {
                await editor.LoadAsync();
                continue;
            }

            _documents.TabPages.Remove(page);
            page.Dispose();
            if (newName is not null)
            {
                await OpenDesignAsync(session, obj with { Name = newName, TableName = obj.Type == SchemaObjectType.Table ? newName : obj.TableName });
            }
        }
    }

    private async Task DropObjectAsync(DatabaseSession session, SchemaObject obj)
    {
        if ((obj.Type is SchemaObjectType.Table or SchemaObjectType.View && !EnsureNoPendingData(session, obj.Name))
            || (obj.Type == SchemaObjectType.Index && !EnsureNoUnappliedEditor(session, obj)))
        {
            return;
        }

        var kind = SchemaObjectCommands.TypeKeyword(obj.Type).ToLowerInvariant();
        var details = $"Drop the {kind} \"{obj.Name}\"?";
        if (obj.Type == SchemaObjectType.Table)
        {
            try
            {
                var rows = await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {Core.Sql.SqlIdentifier.Quote(obj.Name)}");
                details += $"\n\nThis deletes the table, all {rows:N0} of its rows, and its indexes and triggers.";
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                details += "\n\nThis deletes the table and all of its data.";
            }
        }

        if (MessageBox.Show(this, details + "\n\nThis cannot be undone.", "Drop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunAsync($"Dropping {obj.Name}...", async () =>
        {
            await new SchemaChangeService(session).ApplyAsync(SchemaObjectCommands.PlanDrop(obj));
            _messages.Info($"Dropped {kind} {obj.Name}.");

            // Close tabs that show the dropped object.
            foreach (var page in _documents.TabPages.Cast<TabPage>().Where(p =>
                         (p.Tag is TableDataView v && v.Session == session && v.ObjectName.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))
                         || (p.Tag is SchemaEditorBase s && s.Session == session && s.ObjectType == obj.Type && string.Equals(s.OriginalName, obj.Name, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _documents.TabPages.Remove(page);
                page.Dispose();
            }

            if (obj.Type == SchemaObjectType.Index)
            {
                await RefreshEditorsAfterChangeAsync(session, obj, null);
            }

            await OnSchemaChangedAsync(session);
        });
    }

    // ---- Query tabs ---------------------------------------------------------------------------

    /// <summary>The database a new query tab should use: the explorer selection, else the active tab's, else the first open one.</summary>
    private DatabaseSession? DefaultSession() =>
        _explorer.SelectedSession ?? ActiveQuery?.Session ?? _databases.Sessions.FirstOrDefault();

    private async Task<QueryDocument> NewQueryAsync(DatabaseSession? session = null, string? text = null, string? filePath = null, System.Text.Encoding? encoding = null)
    {
        session ??= DefaultSession();
        var doc = new QueryDocument(session, $"SQLQuery{++_untitledCounter}.sql", () => _settings.QueryRowLimit) { Dock = DockStyle.Fill };
        if (text is not null)
        {
            doc.LoadText(text, filePath, encoding);
        }

        var page = new TabPage { Tag = doc };
        page.Controls.Add(doc);
        doc.StateChanged += (_, _) =>
        {
            UpdateQueryTabText(page, doc);
            if (ActiveQuery == doc)
            {
                UpdateCommandState();
            }
        };
        doc.QueryCompleted += async (_, e) => await OnQueryCompletedAsync(e);
        doc.ExportRequested += (_, e) => OpenExportWizard(e.Session.FilePath, null, e.Sql);
        UpdateQueryTabText(page, doc);

        _documents.TabPages.Add(page);
        _documents.SelectedTab = page;
        _ribbon.SelectTab(_queryTab);
        UpdateCommandState();
        doc.FocusEditor();
        await doc.RefreshSchemaAsync();
        return doc;
    }

    private static void UpdateQueryTabText(TabPage page, QueryDocument doc)
    {
        var database = doc.Session is null ? "no database" : doc.Session.DisplayName;
        page.Text = $"{doc.Title}{(doc.IsModified ? "*" : string.Empty)}{(doc.IsRunning ? " (running)" : string.Empty)}  [{database}]";
        page.ToolTipText = doc.FilePath ?? doc.Title;
    }

    private async Task ExecuteActiveAsync(ExecuteMode mode)
    {
        if (ActiveQuery is { IsRunning: false } query)
        {
            await query.ExecuteAsync(mode);
        }
    }

    private async Task OnQueryCompletedAsync(QueryCompletedEventArgs e)
    {
        var summary = e.Summary;
        if (summary.StatementCount > 0)
        {
            var firstError = summary.Results.FirstOrDefault(r => r.Error is not null)?.Error;
            var entry = new QueryHistoryEntry(
                DateTime.Now,
                e.Session.FilePath,
                e.Script,
                (long)summary.Duration.TotalMilliseconds,
                summary.Succeeded,
                summary.TotalRows,
                summary.Cancelled ? "Cancelled" : firstError);
            _historyView.Add(entry, _history.MaxEntries);
            try
            {
                await _history.AppendAsync(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError("Could not save query history", ex, showDialog: false);
            }
        }

        if (summary.ChangedSchema && e.Session.IsOpen)
        {
            await OnSchemaChangedAsync(e.Session);
        }
    }

    private async Task OpenSqlFilesAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Open SQL File", Filter = SqlFilter, Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            var existing = _documents.TabPages.Cast<TabPage>().FirstOrDefault(p =>
                p.Tag is QueryDocument d && string.Equals(d.FilePath, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _documents.SelectedTab = existing;
                continue;
            }

            await RunAsync($"Opening {Path.GetFileName(file)}...", async () =>
            {
                var content = await SqlFileService.ReadAsync(file);
                await NewQueryAsync(text: content.Text, filePath: Path.GetFullPath(file), encoding: content.Encoding);
            });
        }
    }

    /// <summary>Saves the active query tab. Returns false if the user cancelled or saving failed.</summary>
    private Task<bool> SaveActiveQueryAsync(bool saveAs) =>
        ActiveQuery is { } doc ? SaveQueryAsync(doc, saveAs) : Task.FromResult(false);

    private async Task<bool> SaveQueryAsync(QueryDocument doc, bool saveAs)
    {
        var path = doc.FilePath;
        if (saveAs || path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save SQL File",
                Filter = SqlFilter,
                DefaultExt = "sql",
                AddExtension = true,
                OverwritePrompt = true, // Asks before replacing an existing file.
                FileName = doc.Title,
            };
            if (path is not null)
            {
                dialog.InitialDirectory = Path.GetDirectoryName(path);
            }

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            await doc.SaveAsync(path);
            _messages.Info($"Saved {path}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save the file", ex, showDialog: true);
            return false;
        }
    }

    private async Task OpenHistoryEntryAsync(QueryHistoryEntry entry)
    {
        var session = _databases.Sessions.FirstOrDefault(s => string.Equals(s.FilePath, entry.DatabasePath, StringComparison.OrdinalIgnoreCase))
            ?? DefaultSession();
        await NewQueryAsync(session, entry.Sql);
    }

    private async Task ShowOptionsAsync()
    {
        using var dialog = new OptionsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.QueryRowLimit = dialog.QueryRowLimit;
        var themeChanged = _settings.Theme != dialog.ThemeName;
        _settings.Theme = dialog.ThemeName;
        try
        {
            await _settingsStore.SaveAsync(_settings);
            _messages.Info($"Settings saved. Query row limit: {_settings.QueryRowLimit:N0}.");
            if (themeChanged)
            {
                MessageBox.Show(this, "The new theme is used the next time you start SQLite Manager.", "Options", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save settings", ex, showDialog: true);
        }
    }

    // ---- Database tools -----------------------------------------------------------------------

    /// <summary>The database for a Database Tools command, or null (with a message) if none is open.</summary>
    private DatabaseSession? ToolSession()
    {
        var session = DefaultSession();
        if (session is null)
        {
            MessageBox.Show(this, "Open or create a database first.", "Database Tools", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return session;
    }

    private async Task VacuumAsync()
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        await RunAsync($"Vacuuming {session.DisplayName}...", async () =>
        {
            var result = await new Core.Maintenance.MaintenanceService(session).VacuumAsync();
            _messages.Info($"VACUUM {session.DisplayName}: {result.SizeBefore / 1024.0:N0} KiB → {result.SizeAfter / 1024.0:N0} KiB in {result.Duration.TotalSeconds:N1} s.");
        });
    }

    private async Task AnalyzeAsync()
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        await RunAsync($"Analyzing {session.DisplayName}...", async () =>
        {
            var duration = await new Core.Maintenance.MaintenanceService(session).AnalyzeAsync();
            _messages.Info($"ANALYZE {session.DisplayName}: done in {duration.TotalSeconds:N1} s.");
            await OnSchemaChangedAsync(session);
        });
    }

    private async Task CheckAsync(bool foreignKeys)
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        var name = foreignKeys ? "Foreign key check" : "Integrity check";
        await RunAsync($"{name} on {session.DisplayName}...", async () =>
        {
            var service = new Core.Maintenance.MaintenanceService(session);
            var result = foreignKeys ? await service.ForeignKeyCheckAsync() : await service.IntegrityCheckAsync();
            if (result.IsOk)
            {
                var text = foreignKeys ? "No rows break a foreign key." : "The database is OK.";
                _messages.Info($"{name} on {session.DisplayName}: {text}");
                MessageBox.Show(this, text, $"{name} — {session.DisplayName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var heading = foreignKeys
                ? $"{result.Rows.Rows.Count:N0} row(s) break a foreign key (table, rowid of the row, parent table, foreign key id)."
                : $"SQLite reported {result.Rows.Rows.Count:N0} problem(s).";
            _messages.Error($"{name} on {session.DisplayName}: {heading}");
            var document = new Tools.ResultDocument(heading, result.Rows, isProblem: true) { Dock = DockStyle.Fill };
            var page = new TabPage($"{name}  [{session.DisplayName}]") { Tag = document, ToolTipText = session.FilePath };
            page.Controls.Add(document);
            _documents.TabPages.Add(page);
            _documents.SelectedTab = page;
        });
    }

    private async Task ShowPragmaEditorAsync()
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        var service = new Core.Maintenance.PragmaService(session);
        Core.Maintenance.PragmaValues current;
        try
        {
            current = await service.ReadAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            ShowError("Could not read the PRAGMA settings", ex, showDialog: true);
            return;
        }

        using var dialog = new Tools.PragmaDialog(session.DisplayName, current, session.IsReadOnly);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var wanted = dialog.Wanted;
        await RunAsync($"Changing settings of {session.DisplayName}...", async () =>
        {
            var result = await service.ApplyAsync(wanted);
            var refused = new List<string>();
            if (result.ForeignKeys != wanted.ForeignKeys)
            {
                refused.Add("foreign_keys (it cannot change while a transaction is open)");
            }

            if (!result.JournalMode.Equals(wanted.JournalMode, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add($"journal_mode (still {result.JournalMode})");
            }

            if (refused.Count > 0)
            {
                _messages.Error("SQLite did not change: " + string.Join(", ", refused));
                MessageBox.Show(this, "SQLite did not change:\n\n" + string.Join("\n", refused), "PRAGMA Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                _messages.Info($"PRAGMA settings of {session.DisplayName} changed.");
            }
        });
    }

    private async Task BackupAsync()
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = $"Back Up {session.DisplayName}",
            Filter = DatabaseFilter,
            DefaultExt = Path.GetExtension(session.FilePath).TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true, // Asks before replacing an existing file.
            InitialDirectory = Path.GetDirectoryName(session.FilePath),
            FileName = $"{Path.GetFileNameWithoutExtension(session.FilePath)}-backup-{DateTime.Now:yyyy-MM-dd-HHmm}{Path.GetExtension(session.FilePath)}",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var target = dialog.FileName;
        await RunAsync($"Backing up {session.DisplayName}...", async () =>
        {
            await session.BackupToAsync(target, overwrite: File.Exists(target));
            _messages.Info($"Backed up {session.FilePath} to {target} ({new FileInfo(target).Length / 1024.0:N0} KiB).");
        });
    }

    private async Task AttachAsync()
    {
        if (ToolSession() is not { } session)
        {
            return;
        }

        using var dialog = new OpenFileDialog { Title = $"Attach a Database to {session.DisplayName}", Filter = DatabaseFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var service = new AttachService(session);
        var used = (await service.ListAsync()).Select(a => a.Name);
        var name = Transfer.InputDialog.Ask(this, "Attach Database", "Schema name (use it as name.table in queries):", AttachService.SuggestName(dialog.FileName, used));
        if (name is null)
        {
            return;
        }

        await RunAsync($"Attaching {Path.GetFileName(dialog.FileName)}...", async () =>
        {
            await service.AttachAsync(dialog.FileName, name);
            _messages.Info($"Attached {dialog.FileName} to {session.DisplayName} as {name}. Query it as {name}.table_name.");
            await OnSchemaChangedAsync(session);
        });
    }

    private async Task FillDetachMenuAsync()
    {
        _detachButton.DropDownItems.Clear();
        var session = DefaultSession();
        IReadOnlyList<AttachedDatabase> attached = [];
        if (session is not null)
        {
            try
            {
                attached = await new AttachService(session).ListAsync();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                ShowError("Could not list attached databases", ex, showDialog: false);
            }
        }

        foreach (var database in attached)
        {
            var name = database.Name;
            _detachButton.DropDownItems.Add(new ToolStripMenuItem($"{name}  ({database.FilePath})", null, async (_, _) => await DetachAsync(session!, name)));
        }

        if (_detachButton.DropDownItems.Count == 0)
        {
            _detachButton.DropDownItems.Add(new ToolStripMenuItem("(nothing attached)") { Enabled = false });
        }
    }

    private Task DetachAsync(DatabaseSession session, string name) =>
        RunAsync($"Detaching {name}...", async () =>
        {
            await new AttachService(session).DetachAsync(name);
            _messages.Info($"Detached {name} from {session.DisplayName}.");
            await OnSchemaChangedAsync(session);
        });

    // ---- Window layout ------------------------------------------------------------------------

    /// <summary>Restores the saved window position if it is still on a screen.</summary>
    private void ApplyWindowBounds()
    {
        if (_settings.Window is not { } layout)
        {
            return;
        }

        var bounds = new Rectangle(layout.X, layout.Y, layout.Width, layout.Height);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }

        if (layout.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private void ApplyPaneLayout()
    {
        var layout = _settings.Window ?? new WindowLayout();
        _mainSplit.SplitterDistance = Math.Clamp(LogicalToDeviceUnits(layout.ExplorerWidth), 50, Math.Max(50, _mainSplit.Width - 200));
        _workSplit.SplitterDistance = Math.Max(100, _workSplit.Height - LogicalToDeviceUnits(layout.OutputHeight));
        _mainSplit.Panel1Collapsed = !layout.ExplorerVisible;
        _workSplit.Panel2Collapsed = !layout.OutputVisible;
        _explorerToggle.Checked = layout.ExplorerVisible;
        _outputToggle.Checked = layout.OutputVisible;
    }

    private int ToLogical(int devicePixels) => (int)Math.Round(devicePixels * 96.0 / DeviceDpi);

    private void SaveWindowLayout()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Window = new WindowLayout
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
            ExplorerWidth = _mainSplit.Panel1Collapsed ? _settings.Window?.ExplorerWidth ?? 280 : ToLogical(_mainSplit.SplitterDistance),
            OutputHeight = _workSplit.Panel2Collapsed ? _settings.Window?.OutputHeight ?? 200 : ToLogical(_workSplit.Height - _workSplit.SplitterDistance),
            ExplorerVisible = !_mainSplit.Panel1Collapsed,
            OutputVisible = !_workSplit.Panel2Collapsed,
        };
    }

    // ---- Query designs ------------------------------------------------------------------------

    private async Task<QueryDesigner.QueryDesignerDocument?> NewQueryDesignAsync(DatabaseSession? session, string? table, Core.QueryDesign.QueryDesignDocument? design = null, string? filePath = null)
    {
        session ??= DefaultSession();
        if (session is null)
        {
            MessageBox.Show(this, "Open or create a database first.", "Query Design", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        var doc = new QueryDesigner.QueryDesignerDocument(session, $"Query{++_untitledDesignCounter}.qdesign", () => _settings.QueryRowLimit) { Dock = DockStyle.Fill };
        if (design is not null)
        {
            doc.LoadDesign(design, filePath);
        }

        var page = new TabPage { Tag = doc };
        page.Controls.Add(doc);
        void UpdateText()
        {
            var text = $"{doc.Title}{(doc.IsModified ? "*" : string.Empty)}{(doc.IsRunning ? " (running)" : string.Empty)}  [{session.DisplayName}]";
            if (page.Text != text)
            {
                page.Text = text;
            }

            page.ToolTipText = doc.FilePath ?? doc.Title;
        }

        doc.StateChanged += (_, _) =>
        {
            UpdateText();
            if (ActiveQueryDesign == doc)
            {
                UpdateCommandState();
            }
        };
        doc.Message += (_, m) =>
        {
            if (m.IsError)
            {
                _messages.Error(m.Text);
            }
            else
            {
                _messages.Info(m.Text);
            }
        };
        doc.SendToQueryRequested += async (_, sql) => await NewQueryAsync(session, sql);
        doc.ExportRequested += (_, sql) => OpenExportWizard(session.FilePath, null, sql);
        UpdateText();
        _documents.TabPages.Add(page);
        _documents.SelectedTab = page;
        _ribbon.SelectTab(_queryDesignTab);
        UpdateCommandState();

        await RunAsync("Reading the schema...", async () =>
        {
            await doc.RefreshSchemaAsync();
            if (table is not null)
            {
                await doc.AddTableAsync(table, null);
            }
        });
        return doc;
    }

    private async Task OpenQueryDesignsAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Open Query Design", Filter = DesignFilter, Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            var path = Path.GetFullPath(file);
            var existing = _documents.TabPages.Cast<TabPage>().FirstOrDefault(p =>
                p.Tag is QueryDesigner.QueryDesignerDocument d && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _documents.SelectedTab = existing;
                continue;
            }

            Core.QueryDesign.QueryDesignDocument design;
            try
            {
                design = await Core.QueryDesign.QueryDesignSerializer.LoadAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                ShowError($"Could not open {Path.GetFileName(path)}", ex, showDialog: true);
                continue;
            }

            // Use the database the design was saved with if it is open, else the current one.
            var session = _databases.Sessions.FirstOrDefault(s => string.Equals(s.FilePath, design.Database, StringComparison.OrdinalIgnoreCase))
                ?? DefaultSession();
            await NewQueryDesignAsync(session, null, design, path);
        }
    }

    private Task<bool> SaveActiveQueryDesignAsync(bool saveAs) =>
        ActiveQueryDesign is { } doc ? SaveQueryDesignAsync(doc, saveAs) : Task.FromResult(false);

    private async Task<bool> SaveQueryDesignAsync(QueryDesigner.QueryDesignerDocument doc, bool saveAs)
    {
        var path = doc.FilePath;
        if (saveAs || path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save Query Design",
                Filter = DesignFilter,
                DefaultExt = "qdesign",
                AddExtension = true,
                OverwritePrompt = true, // Asks before replacing an existing file.
                FileName = doc.Title,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            await doc.SaveAsync(path);
            _messages.Info($"Saved {path}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save the design", ex, showDialog: true);
            return false;
        }
    }

    private async Task<bool> ConfirmCloseQueryDesignAsync(QueryDesigner.QueryDesignerDocument doc, TabPage page)
    {
        if (!doc.IsModified)
        {
            return true;
        }

        _documents.SelectedTab = page;
        var answer = MessageBox.Show(this, $"Save changes to {doc.Title}?", "SQLite Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => await SaveQueryDesignAsync(doc, saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    // ---- Document tabs ------------------------------------------------------------------------

    private async void OnDocumentsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle)
        {
            return;
        }

        for (var i = 0; i < _documents.TabCount; i++)
        {
            if (_documents.GetTabRect(i).Contains(e.Location))
            {
                await CloseDocumentAsync(_documents.TabPages[i]);
                return;
            }
        }
    }

    /// <summary>Closes a tab. Asks to save a modified query first. Returns false if the user cancelled.</summary>
    private async Task<bool> CloseDocumentAsync(TabPage? page)
    {
        if (page is null)
        {
            return true;
        }

        if (page.Tag is QueryDocument doc)
        {
            if (!await ConfirmCloseQueryAsync(doc, page))
            {
                return false;
            }

            if (doc.IsRunning)
            {
                doc.Cancel();
                await doc.WaitUntilIdleAsync();
            }
        }
        else if (page.Tag is QueryDesigner.QueryDesignerDocument design)
        {
            if (!await ConfirmCloseQueryDesignAsync(design, page))
            {
                return false;
            }

            design.Cancel();
        }
        else if (page.Tag is Transfer.JobsDocument { IsRunning: true })
        {
            MessageBox.Show(this, "A job is running. Wait for it to finish, or press Cancel in the Jobs tab.", "Jobs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        else if (page.Tag is SchemaEditorBase editor)
        {
            _documents.SelectedTab = page;
            if (!await editor.ConfirmCloseAsync())
            {
                return false;
            }
        }
        else if (page.Tag is TableDataView data)
        {
            _documents.SelectedTab = page;
            if (!await data.ConfirmCloseAsync())
            {
                return false;
            }
        }

        _documents.TabPages.Remove(page);
        page.Dispose();
        UpdateCommandState();
        return true;
    }

    private async Task<bool> ConfirmCloseQueryAsync(QueryDocument doc, TabPage page)
    {
        if (!doc.IsModified)
        {
            return true;
        }

        _documents.SelectedTab = page;
        var answer = MessageBox.Show(this, $"Save changes to {doc.Title}?", "SQLite Manager",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => await SaveQueryAsync(doc, saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    private async Task<bool> CloseAllDocumentsAsync()
    {
        foreach (var page in _documents.TabPages.Cast<TabPage>().ToList())
        {
            if (!await CloseDocumentAsync(page))
            {
                return false;
            }
        }

        return true;
    }

    private void ShowOutput(Control pane)
    {
        if (_workSplit.Panel2Collapsed)
        {
            _workSplit.Panel2Collapsed = false;
            _outputToggle.Checked = true;
        }

        _outputTabs.SelectedTab = (TabPage)pane.Parent!;
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>Runs a UI action with a status message. Errors are reported instead of thrown.</summary>
    private async Task RunAsync(string status, Func<Task> action)
    {
        _statusText.Text = status;
        UseWaitCursor = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(status.TrimEnd('.'), ex, showDialog: true);
        }
        finally
        {
            UseWaitCursor = false;
            _statusText.Text = "Ready";
            UpdateCommandState();
        }
    }

    private void ShowError(string context, Exception ex, bool showDialog)
    {
        _messages.Error($"{context}: {ex.Message}");
        if (showDialog && !_closing)
        {
            MessageBox.Show(this, ex.Message, context, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closing || e.Cancel)
        {
            return;
        }

        // Ask about unsaved queries and close all databases before the window goes away, then close again.
        e.Cancel = true;
        if (!await CloseAllDocumentsAsync())
        {
            return;
        }

        _closing = true;
        SaveWindowLayout();
        await SaveSettingsAsync();
        try
        {
            await _databases.DisposeAsync();
        }
        catch (Exception ex)
        {
            _messages.Error("Error while closing databases: " + ex.Message);
        }

        Close();
    }

    private sealed record SessionItem(DatabaseSession Session)
    {
        public override string ToString() => Session.DisplayName + (Session.IsReadOnly ? " (read-only)" : string.Empty);
    }
}
