using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.Documents;

/// <summary>
/// Virtual-mode grid over a table or view with filters, sorting, and pending edits.
/// Rows 0..BaseRowCount-1 come from the database; new (pending) rows follow them.
/// </summary>
internal sealed class TableDataView : UserControl
{
    private const int PageSize = 500;
    private const int MaxRowsPerAction = 100_000;

    private readonly TableDataService _service;
    private readonly SqlDataGrid _grid;
    private readonly FilterBar _filters;
    private readonly ToolStripStatusLabel _rowCountLabel;
    private readonly ToolStripStatusLabel _pendingLabel;
    private readonly ToolStripStatusLabel _stateLabel;
    private readonly ContextMenuStrip _menu;
    private TableSnapshot? _snapshot;
    private PagedRowCache<TableRow>? _cache;
    private PendingChanges? _pending;
    private ColumnAffinity[] _affinities = [];
    private bool[] _readOnlyColumns = [];
    private string? _sortColumn;
    private SortDirection _sortDirection;
    private int _baseRowCount;

    public TableDataView(DatabaseSession session, string objectName)
    {
        Session = session;
        ObjectName = objectName;
        _service = new TableDataService(session);

        _grid = new SqlDataGrid
        {
            Dock = DockStyle.Fill,
            VirtualMode = true,
            SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            CellStateProvider = GetCellState,
        };
        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellValuePushed += OnCellValuePushed;
        _grid.CellBeginEdit += OnCellBeginEdit;
        _grid.EditingControlShowing += OnEditingControlShowing;
        _grid.CellParsing += (_, e) =>
        {
            e.Value = e.Value?.ToString() ?? string.Empty;
            e.ParsingApplied = true;
        };
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.ColumnHeaderMouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                await ToggleSortAsync(e.ColumnIndex);
            }
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && GetCellValue(e.RowIndex, e.ColumnIndex) is byte[])
            {
                ShowBlobViewer(e.RowIndex, e.ColumnIndex);
            }
        };
        _grid.KeyDown += OnGridKeyDown;
        _grid.CellMouseDown += (_, e) =>
        {
            // Right-click selects the cell under the mouse unless it is already part of the selection.
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0 && !_grid[e.ColumnIndex, e.RowIndex].Selected)
            {
                _grid.CurrentCell = _grid[e.ColumnIndex, e.RowIndex];
            }
        };
        _grid.CurrentCellChanged += (_, _) => OnStateChanged();

        _filters = new FilterBar(_grid);
        _filters.FiltersChanged += async (_, _) => await ReloadAsync();

        _rowCountLabel = new ToolStripStatusLabel("Loading...");
        _pendingLabel = new ToolStripStatusLabel { ForeColor = Theme.WarningText };
        _stateLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        var footer = new StatusStrip
        {
            Dock = DockStyle.Bottom,
            SizingGrip = false,
            BackColor = Theme.RibbonBody,
            Renderer = new RibbonRenderer(),
        };
        footer.Items.AddRange([_rowCountLabel, _pendingLabel, _stateLabel]);

        _menu = BuildContextMenu();
        _grid.ContextMenuStrip = _menu;

        Controls.Add(_grid);
        Controls.Add(_filters);
        Controls.Add(footer);
    }

    /// <summary>Raised when pending changes, selection, or read-only state change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised with a message for the main window's Messages pane.</summary>
    public event EventHandler<(string Text, bool IsError)>? Message;

    public DatabaseSession Session { get; }

    public string ObjectName { get; }

    /// <summary>Why the data cannot be edited, or null if it can.</summary>
    public string? ReadOnlyReason { get; private set; } = "Loading";

    public bool CanEdit => ReadOnlyReason is null;

    public bool HasPendingChanges => _pending?.HasChanges == true;

    public int PendingCount => _pending?.Count ?? 0;

    public bool HasFilters => _filters.HasFilters;

    public bool HasCurrentCell => _grid.CurrentCell is not null;

    // ---- Loading ----------------------------------------------------------------------------

    /// <summary>Reads the table's structure and the first page.</summary>
    public async Task LoadAsync()
    {
        var snapshot = await _service.OpenAsync(ObjectName, CurrentQuery());
        var first = await _service.GetPageAsync(snapshot, 0, PageSize);

        var details = snapshot.Details;
        _affinities = details.Columns.Select(c => ValueParser.GetAffinity(c.DeclaredType)).ToArray();
        _readOnlyColumns = details.Columns.Select(c => c.IsGenerated).ToArray();
        ReadOnlyReason = Session.IsReadOnly ? "Read-only database"
            : snapshot.IsView ? "Views are read-only"
            : snapshot.KeyKind == RowKeyKind.None ? "Read-only: the table has no usable rowid or primary key"
            : null;
        if (CanEdit)
        {
            _pending = new PendingChanges(EditTarget.FromSnapshot(snapshot));
            _pending.Changed += (_, _) =>
            {
                _grid.Invalidate();
                UpdateFooter();
                OnStateChanged();
            };
        }

        _grid.SetColumns(details.Columns.Select(c => new ResultColumn(c.Name, c.DeclaredType)).ToList(), first.Select(r => r.Values).ToList());
        for (var i = 0; i < _grid.Columns.Count; i++)
        {
            var column = _grid.Columns[i];
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
            column.ReadOnly = _readOnlyColumns[i];
            column.Tag = details.Columns[i].Name; // The filter bar uses the real name.
            if (details.Columns[i].IsPrimaryKey)
            {
                column.HeaderText = "🔑 " + column.HeaderText;
            }
        }

        _grid.ReadOnly = !CanEdit;
        _filters.Rebuild();
        Apply(snapshot, first, keepRow: 0);
    }

    /// <summary>Reads the data again with the current filters and sort order. Pending changes are kept.</summary>
    public async Task ReloadAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        try
        {
            _stateLabel.Text = "Loading...";
            var keepRow = Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
            var snapshot = await _service.OpenAsync(ObjectName, CurrentQuery());
            var firstPage = keepRow / PageSize;
            var rows = await _service.GetPageAsync(snapshot, (long)firstPage * PageSize, PageSize);
            Apply(snapshot, rows, keepRow, firstPage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _stateLabel.Text = "Error: " + ex.Message;
            _stateLabel.ForeColor = Theme.ErrorText;
            Message?.Invoke(this, ($"{ObjectName}: {ex.Message}", true));
        }
    }

    private TableQuery CurrentQuery() => new(_filters.GetFilters(), _sortColumn, _sortDirection);

    private void Apply(TableSnapshot snapshot, IReadOnlyList<TableRow> seedRows, int keepRow, int seedPage = 0)
    {
        _cache?.Clear();
        var cache = new PagedRowCache<TableRow>((offset, limit, ct) => _service.GetPageAsync(snapshot, offset, limit, ct), PageSize);
        cache.Seed(seedPage, seedRows);
        cache.PageLoaded += (_, _) => RunOnUi(() => _grid.Invalidate());
        cache.PageLoadFailed += (_, e) => RunOnUi(() =>
        {
            _stateLabel.Text = "Error: " + e.Error.Message;
            _stateLabel.ForeColor = Theme.ErrorText;
            _grid.Invalidate();
        });

        _snapshot = snapshot;
        _cache = cache;
        _baseRowCount = (int)Math.Min(snapshot.RowCount, int.MaxValue - 1_000_000);
        _grid.SetRowCount(TotalRowCount);
        if (keepRow > 0 && keepRow < _grid.RowCount)
        {
            _grid.FirstDisplayedScrollingRowIndex = keepRow;
        }

        _grid.Invalidate();
        _stateLabel.Text = ReadOnlyReason ?? string.Empty;
        _stateLabel.ForeColor = Theme.RibbonGroupText;
        UpdateFooter();
        OnStateChanged();
    }

    private int TotalRowCount => _baseRowCount + (_pending?.InsertedRowCount ?? 0);

    private void UpdateFooter()
    {
        if (_snapshot is null)
        {
            return;
        }

        _rowCountLabel.Text = _snapshot.Query.IsFiltered
            ? $"{_snapshot.RowCount:N0} of {_snapshot.TotalRowCount:N0} rows (filtered)"
            : $"{_snapshot.RowCount:N0} rows";
        _pendingLabel.Text = PendingCount switch
        {
            0 => string.Empty,
            1 => "·  1 pending change",
            var n => $"·  {n:N0} pending changes",
        };
    }

    // ---- Sorting and filters ----------------------------------------------------------------

    private async Task ToggleSortAsync(int columnIndex)
    {
        if (_snapshot is null || columnIndex < 0)
        {
            return;
        }

        var name = _snapshot.Details.Columns[columnIndex].Name;
        (_sortColumn, _sortDirection) = _sortColumn == name
            ? _sortDirection switch
            {
                SortDirection.Ascending => (name, SortDirection.Descending),
                _ => ((string?)null, SortDirection.None),
            }
            : (name, SortDirection.Ascending);

        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = SortOrder.None;
        }

        if (_sortColumn is not null)
        {
            _grid.Columns[columnIndex].HeaderCell.SortGlyphDirection =
                _sortDirection == SortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
        }

        await ReloadAsync();
    }

    public async Task ClearFiltersAsync()
    {
        if (_filters.Clear())
        {
            await ReloadAsync();
        }
    }

    // ---- Cell values ------------------------------------------------------------------------

    private bool IsInsertedRow(int rowIndex) => rowIndex >= _baseRowCount;

    private TableRow? TryGetBaseRow(int rowIndex) =>
        _cache is not null && rowIndex < _baseRowCount && _cache.TryGetRow(rowIndex, out var row) ? row : null;

    /// <summary>Loads the row's page if needed.</summary>
    private async Task<TableRow?> GetBaseRowAsync(int rowIndex)
    {
        if (_cache is null || rowIndex >= _baseRowCount)
        {
            return null;
        }

        if (!_cache.TryGetRow(rowIndex, out var row))
        {
            await _cache.LoadPageAsync(rowIndex / PageSize);
            _cache.TryGetRow(rowIndex, out row);
        }

        return row;
    }

    /// <summary>The value shown in a cell, including pending edits. <see cref="SqlDataGrid.Pending"/> if not loaded yet.</summary>
    private object? GetCellValue(int rowIndex, int columnIndex)
    {
        if (IsInsertedRow(rowIndex))
        {
            var inserted = _pending!.InsertedRows[rowIndex - _baseRowCount];
            return inserted.IsSet(columnIndex) ? inserted[columnIndex] : SqlDataGrid.DefaultValue;
        }

        var row = TryGetBaseRow(rowIndex);
        if (row is null)
        {
            return SqlDataGrid.Pending;
        }

        return row.Key is not null && _pending is not null && _pending.TryGetValue(row.Key, columnIndex, out var value)
            ? value
            : row.Values[columnIndex];
    }

    private CellState GetCellState(int rowIndex, int columnIndex)
    {
        if (_pending is null || rowIndex >= TotalRowCount)
        {
            return CellState.None;
        }

        if (IsInsertedRow(rowIndex))
        {
            return CellState.RowInserted;
        }

        var key = TryGetBaseRow(rowIndex)?.Key;
        if (key is null)
        {
            return CellState.None;
        }

        if (_pending.IsDeleted(key))
        {
            return CellState.RowDeleted;
        }

        var state = _pending.IsRowChanged(key) ? CellState.RowModified : CellState.None;
        if (columnIndex >= 0 && _pending.IsCellChanged(key, columnIndex))
        {
            state |= CellState.Changed;
        }

        return state;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < TotalRowCount && e.ColumnIndex < _affinities.Length)
        {
            e.Value = GetCellValue(e.RowIndex, e.ColumnIndex);
        }
    }

    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        var reason = WhyCellNotEditable(e.RowIndex, e.ColumnIndex);
        if (reason is not null)
        {
            e.Cancel = true;
            _stateLabel.Text = reason;
            if (GetCellValue(e.RowIndex, e.ColumnIndex) is byte[])
            {
                BeginInvoke(() => ShowBlobViewer(e.RowIndex, e.ColumnIndex));
            }
        }
    }

    /// <summary>
    /// Why a cell cannot be changed, or null if it can. With <paramref name="byTyping"/>, BLOB cells are
    /// also refused because they are changed in the BLOB viewer.
    /// </summary>
    private string? WhyCellNotEditable(int rowIndex, int columnIndex, bool byTyping = true)
    {
        if (!CanEdit)
        {
            return ReadOnlyReason;
        }

        if (_readOnlyColumns[columnIndex])
        {
            return "Generated columns cannot be edited.";
        }

        if (!IsInsertedRow(rowIndex))
        {
            var row = TryGetBaseRow(rowIndex);
            if (row?.Key is null)
            {
                return "The row is still loading.";
            }

            if (_pending!.IsDeleted(row.Key))
            {
                return "The row is marked for deletion. Revert it to edit.";
            }
        }

        return byTyping && GetCellValue(rowIndex, columnIndex) is byte[] ? "Use the BLOB viewer to change BLOB values." : null;
    }

    private void OnEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        // Edit the raw value (numbers in invariant form), not the display text such as "NULL".
        if (e.Control is TextBox box && _grid.CurrentCell is { } cell)
        {
            var value = GetCellValue(cell.RowIndex, cell.ColumnIndex);
            var text = value is null || ReferenceEquals(value, SqlDataGrid.DefaultValue) || ReferenceEquals(value, SqlDataGrid.Pending)
                ? string.Empty
                : ValueParser.FormatForEdit(value);

            // When editing starts with a keystroke the box already holds that key; keep it.
            if (!_grid.IsCurrentCellDirty)
            {
                box.Text = text;
                box.SelectAll();
            }
        }
    }

    private void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        var parsed = ValueParser.Parse(e.Value as string ?? string.Empty, _affinities[e.ColumnIndex]);
        SetCellValue(e.RowIndex, e.ColumnIndex, parsed);
    }

    private void SetCellValue(int rowIndex, int columnIndex, object? value)
    {
        if (_pending is null)
        {
            return;
        }

        if (IsInsertedRow(rowIndex))
        {
            _pending.SetInsertedValue(rowIndex - _baseRowCount, columnIndex, value);
            return;
        }

        var row = TryGetBaseRow(rowIndex);
        if (row?.Key is not null && !_pending.IsDeleted(row.Key))
        {
            _pending.SetValue(row.Key, columnIndex, row.Values[columnIndex], value);
        }
    }

    // ---- Commands ---------------------------------------------------------------------------

    public void AddRow()
    {
        if (_pending is null)
        {
            return;
        }

        _pending.AddRow();
        _grid.SetRowCount(TotalRowCount);
        var column = Enumerable.Range(0, _grid.ColumnCount).FirstOrDefault(i => !_readOnlyColumns[i]);
        _grid.CurrentCell = _grid[column, TotalRowCount - 1];
        _grid.Focus();
    }

    /// <summary>Marks the selected rows for deletion (new rows are removed at once).</summary>
    public async Task DeleteSelectedRowsAsync()
    {
        if (_pending is null)
        {
            return;
        }

        var rows = SelectedRowIndexes();
        if (rows is null)
        {
            return;
        }

        _grid.EndEdit();
        foreach (var rowIndex in rows.OrderDescending())
        {
            if (IsInsertedRow(rowIndex))
            {
                _pending.RemoveInsertedRow(rowIndex - _baseRowCount);
            }
            else if ((await GetBaseRowAsync(rowIndex))?.Key is { } key)
            {
                _pending.Delete(key);
            }
        }

        _grid.SetRowCount(TotalRowCount);
    }

    /// <summary>Sets every editable selected cell to NULL.</summary>
    public async Task SetSelectionToNullAsync()
    {
        if (_pending is null)
        {
            return;
        }

        var cells = SelectedCells();
        if (cells is null)
        {
            return;
        }

        _grid.EndEdit();
        var skipped = 0;
        foreach (var (rowIndex, columnIndex) in cells)
        {
            if (!IsInsertedRow(rowIndex))
            {
                await GetBaseRowAsync(rowIndex);
            }

            if (WhyCellNotEditable(rowIndex, columnIndex, byTyping: false) is null)
            {
                SetCellValue(rowIndex, columnIndex, null);
            }
            else
            {
                skipped++;
            }
        }

        _stateLabel.Text = skipped > 0 ? $"{skipped:N0} cell(s) could not be set to NULL." : string.Empty;
    }

    /// <summary>Removes pending changes from the selected rows.</summary>
    public async Task RevertSelectedRowsAsync()
    {
        if (_pending is null)
        {
            return;
        }

        var rows = SelectedRowIndexes();
        if (rows is null)
        {
            return;
        }

        _grid.CancelEdit();
        foreach (var rowIndex in rows.OrderDescending())
        {
            if (IsInsertedRow(rowIndex))
            {
                _pending.RemoveInsertedRow(rowIndex - _baseRowCount);
            }
            else if ((await GetBaseRowAsync(rowIndex))?.Key is { } key)
            {
                _pending.RevertRow(key);
            }
        }

        _grid.SetRowCount(TotalRowCount);
    }

    /// <summary>Asks, then discards all pending changes.</summary>
    public void RevertAll()
    {
        if (_pending is not { HasChanges: true })
        {
            return;
        }

        var answer = MessageBox.Show(this, $"Discard {DescribePending()}?\n\nThe changes have not been written to the database.",
            "Revert Changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _grid.CancelEdit();
        _pending.Clear();
        _grid.SetRowCount(TotalRowCount);
        Message?.Invoke(this, ($"{ObjectName}: pending changes discarded.", false));
    }

    /// <summary>Writes all pending changes in one transaction. Returns false if nothing was written.</summary>
    public async Task<bool> WriteChangesAsync()
    {
        if (_pending is null)
        {
            return true;
        }

        _grid.EndEdit();
        if (!_pending.HasChanges)
        {
            return true;
        }

        if (_pending.DeletedRowCount > 0)
        {
            var answer = MessageBox.Show(this,
                $"Write {DescribePending()} to {ObjectName}?\n\n{_pending.DeletedRowCount:N0} row(s) will be deleted. This cannot be undone.",
                "Write Changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return false;
            }
        }

        var description = DescribePending();
        try
        {
            _stateLabel.Text = "Writing...";
            var result = await Session.ExecuteBatchAsync(_pending.BuildCommands());
            _pending.Clear();
            Message?.Invoke(this, ($"{ObjectName}: wrote {description}.", false));
            if (result.InsideUserTransaction)
            {
                Message?.Invoke(this, ($"{ObjectName}: the changes are part of an open transaction (BEGIN in a query tab). Run COMMIT to keep them.", true));
            }

            await ReloadAsync();
            return true;
        }
        catch (Exception ex) when (ex is BatchExecutionException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            _stateLabel.Text = "Write failed.";
            _stateLabel.ForeColor = Theme.ErrorText;
            Message?.Invoke(this, ($"{ObjectName}: {ex.Message}", true));
            MessageBox.Show(this, ex.Message, "Write Changes", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>Before closing: asks to write or discard pending changes. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        _grid.EndEdit();
        if (!HasPendingChanges)
        {
            return true;
        }

        var answer = MessageBox.Show(this, $"{ObjectName} has {DescribePending()} that have not been written.\n\nWrite them to the database now?",
            "SQLite Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => await WriteChangesAsync(),
            DialogResult.No => true,
            _ => false,
        };
    }

    /// <summary>Copies the selected cells as tab-separated text with column headers.</summary>
    public async Task CopySelectionAsync()
    {
        var cells = SelectedCells();
        if (cells is null || cells.Count == 0)
        {
            return;
        }

        var rows = cells.Select(c => c.Row).Distinct().Order().ToList();
        var columns = cells.Select(c => c.Column).Distinct().OrderBy(c => _grid.Columns[c].DisplayIndex).ToList();
        var selected = cells.ToHashSet();
        var data = new List<IReadOnlyList<object?>>();
        foreach (var rowIndex in rows)
        {
            if (!IsInsertedRow(rowIndex))
            {
                await GetBaseRowAsync(rowIndex);
            }

            data.Add(columns.Select(c =>
            {
                var value = selected.Contains((rowIndex, c)) ? GetCellValue(rowIndex, c) : null;
                return ReferenceEquals(value, SqlDataGrid.DefaultValue) || ReferenceEquals(value, SqlDataGrid.Pending) ? null : value;
            }).ToList());
        }

        var headers = columns.Select(c => _snapshot!.Details.Columns[c].Name).ToList();
        Clipboard.SetText(ClipboardFormatter.ToTsv(headers, data));
        _stateLabel.Text = $"Copied {rows.Count:N0} row(s) × {columns.Count} column(s).";
    }

    /// <summary>Opens the BLOB viewer for the current cell.</summary>
    public void ShowBlobViewerForCurrentCell()
    {
        if (_grid.CurrentCell is { } cell)
        {
            ShowBlobViewer(cell.RowIndex, cell.ColumnIndex);
        }
    }

    private void ShowBlobViewer(int rowIndex, int columnIndex)
    {
        var value = GetCellValue(rowIndex, columnIndex);
        if (ReferenceEquals(value, SqlDataGrid.Pending))
        {
            return;
        }

        if (ReferenceEquals(value, SqlDataGrid.DefaultValue))
        {
            value = null;
        }

        var editable = WhyCellNotEditable(rowIndex, columnIndex, byTyping: false) is null;
        var column = _snapshot!.Details.Columns[columnIndex].Name;
        using var dialog = new BlobViewerDialog($"{ObjectName}.{column} — row {rowIndex + 1:N0}", value, editable);
        if (dialog.ShowDialog(this) != DialogResult.OK || !editable)
        {
            return;
        }

        switch (dialog.Action)
        {
            case BlobEditAction.SetNull:
                SetCellValue(rowIndex, columnIndex, null);
                break;
            case BlobEditAction.SetBytes:
                SetCellValue(rowIndex, columnIndex, dialog.NewBytes);
                break;
        }
    }

    // ---- Selection helpers ------------------------------------------------------------------

    /// <summary>Selected cells as (row, column), or null (with a message) if the selection is too large.</summary>
    private List<(int Row, int Column)>? SelectedCells()
    {
        if (_grid.GetCellCount(DataGridViewElementStates.Selected) > MaxRowsPerAction)
        {
            MessageBox.Show(this, $"The selection is too large. Select at most {MaxRowsPerAction:N0} cells, or use a query.",
                "SQLite Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        var cells = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(c => c.RowIndex >= 0 && c.ColumnIndex >= 0 && c.RowIndex < TotalRowCount)
            .Select(c => (c.RowIndex, c.ColumnIndex))
            .ToList();
        if (cells.Count == 0 && _grid.CurrentCell is { } current)
        {
            cells.Add((current.RowIndex, current.ColumnIndex));
        }

        return cells;
    }

    private List<int>? SelectedRowIndexes() => SelectedCells()?.Select(c => c.Row).Distinct().ToList();

    private string DescribePending()
    {
        if (_pending is null)
        {
            return "no changes";
        }

        var parts = new List<string>();
        if (_pending.UpdatedRowCount > 0)
        {
            parts.Add($"{_pending.UpdatedRowCount:N0} changed row(s)");
        }

        if (_pending.InsertedRowCount > 0)
        {
            parts.Add($"{_pending.InsertedRowCount:N0} new row(s)");
        }

        if (_pending.DeletedRowCount > 0)
        {
            parts.Add($"{_pending.DeletedRowCount:N0} deleted row(s)");
        }

        return string.Join(", ", parts);
    }

    // ---- Keyboard and menu ------------------------------------------------------------------

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_grid.IsCurrentCellInEditMode)
        {
            return;
        }

        if (e.Control && e.KeyCode == Keys.C)
        {
            e.SuppressKeyPress = true;
            await CopySelectionAsync();
        }
        else if (e.Alt && e.KeyCode == Keys.Delete && CanEdit)
        {
            e.SuppressKeyPress = true;
            await SetSelectionToNullAsync();
        }
        else if (e.KeyCode == Keys.Delete && CanEdit && _grid.SelectedRows.Count > 0)
        {
            e.SuppressKeyPress = true;
            await DeleteSelectedRowsAsync();
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var copy = new ToolStripMenuItem("Copy", null, async (_, _) => await CopySelectionAsync()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var setNull = new ToolStripMenuItem("Set to NULL", null, async (_, _) => await SetSelectionToNullAsync()) { ShortcutKeyDisplayString = "Alt+Del" };
        var blob = new ToolStripMenuItem("BLOB Viewer...", null, (_, _) => ShowBlobViewerForCurrentCell());
        var newRow = new ToolStripMenuItem("New Row", null, (_, _) => AddRow());
        var delete = new ToolStripMenuItem("Delete Rows", null, async (_, _) => await DeleteSelectedRowsAsync()) { ShortcutKeyDisplayString = "Del" };
        var revert = new ToolStripMenuItem("Revert Selected Rows", null, async (_, _) => await RevertSelectedRowsAsync());
        var refresh = new ToolStripMenuItem("Refresh", null, async (_, _) => await ReloadAsync());

        var menu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        menu.Items.AddRange([copy, setNull, blob, new ToolStripSeparator(), newRow, delete, revert, new ToolStripSeparator(), refresh]);
        menu.Opening += (_, _) =>
        {
            setNull.Enabled = CanEdit;
            newRow.Enabled = CanEdit;
            delete.Enabled = CanEdit;
            revert.Enabled = HasPendingChanges;
            blob.Enabled = HasCurrentCell;
        };
        return menu;
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The view was closed while a page was loading.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cache?.Clear();
        }

        base.Dispose(disposing);
    }
}
