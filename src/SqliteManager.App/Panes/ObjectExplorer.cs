using SqliteManager.App.Ui;
using SqliteManager.Core.Data;
using SqliteManager.Core.Schema;

namespace SqliteManager.App.Panes;

/// <summary>Tag on the root node of an open database.</summary>
internal sealed record DatabaseNode(DatabaseSession Session);

/// <summary>Tag on a Tables / Views / Indexes / Triggers folder.</summary>
internal sealed record FolderNode(DatabaseSession Session, SchemaObjectType Type);

/// <summary>Tag on a schema object node.</summary>
internal sealed record ObjectNode(DatabaseSession Session, SchemaObject Object);

/// <summary>Tag on a database attached to a session (ATTACH DATABASE).</summary>
internal sealed record AttachedNode(DatabaseSession Session, string Name);

/// <summary>Tag on the "Loading..." placeholder under a table that has not been expanded yet.</summary>
internal sealed record PlaceholderNode;

internal enum ExplorerCommand
{
    Design,
    ScriptCreate,
    ScriptInsert,
    ScriptDrop,
    NewTable,
    NewView,
    NewTrigger,
    Rename,
    Drop,
    ImportData,
    ExportData,
    NewQueryDesign,
    Detach,
}

internal sealed class ExplorerCommandEventArgs(ExplorerCommand command, DatabaseSession session, SchemaObject? obj, string? newName) : EventArgs
{
    public ExplorerCommand Command { get; } = command;

    public DatabaseSession Session { get; } = session;

    /// <summary>The selected schema object, or null for database and folder nodes.</summary>
    public SchemaObject? Object { get; } = obj;

    /// <summary>The new name for <see cref="ExplorerCommand.Rename"/>, or the attached schema for <see cref="ExplorerCommand.Detach"/>.</summary>
    public string? NewName { get; } = newName;
}

/// <summary>Tree of open databases and their schema objects.</summary>
internal sealed class ObjectExplorer : UserControl
{
    private const string PlaceholderText = "Loading...";

    private readonly TreeView _tree;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openDataItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _closeItem;
    private readonly ToolStripMenuItem _detachItem;

    public ObjectExplorer()
    {
        var header = new Label
        {
            Text = "Object Explorer",
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(26),
            Padding = new Padding(LogicalToDeviceUnits(6), 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.PaneHeader,
            ForeColor = Theme.PaneHeaderText,
            Font = new Font("Segoe UI Semibold", 9f),
        };

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            ShowLines = false,
            FullRowSelect = true,
            ImageList = Icons.CreateTreeImages(LogicalToDeviceUnits(16)),
            ItemHeight = LogicalToDeviceUnits(22),
        };
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _tree.SelectedNode = e.Node;
            }
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is ObjectNode { Object.Type: SchemaObjectType.Table or SchemaObjectType.View })
            {
                OpenDataRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _tree.AfterSelect += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _tree.ItemDrag += (_, e) =>
        {
            // Tables and views can be dropped on a query design.
            if (e.Item is TreeNode { Tag: ObjectNode { Object.Type: SchemaObjectType.Table or SchemaObjectType.View } node })
            {
                var data = new DataObject();
                data.SetData(QueryDesigner.ExplorerDrag.Format, QueryDesigner.ExplorerDrag.Encode(node.Session.Id, node.Object.Name));
                _tree.DoDragDrop(data, DragDropEffects.Copy);
            }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && CanOpenData)
            {
                OpenDataRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F2 && CanRenameOrDrop)
            {
                BeginRename();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && CanRenameOrDrop)
            {
                Raise(ExplorerCommand.Drop);
                e.Handled = true;
            }
        };
        _tree.BeforeLabelEdit += (_, e) => e.CancelEdit = _renaming is null || e.Node != _renaming.Value.Node;
        _tree.AfterLabelEdit += OnAfterLabelEdit;

        var newQueryItem = new ToolStripMenuItem("New Query", null, (_, _) => NewQueryRequested?.Invoke(this, EventArgs.Empty))
        {
            ShortcutKeyDisplayString = "Ctrl+N",
        };
        _openDataItem = new ToolStripMenuItem("Open Data", null, (_, _) => OpenDataRequested?.Invoke(this, EventArgs.Empty));
        _queryDesignItem = new ToolStripMenuItem("New Query Design", null, (_, _) => Raise(ExplorerCommand.NewQueryDesign));
        _designItem = new ToolStripMenuItem("Design...", null, (_, _) => Raise(ExplorerCommand.Design));
        _scriptCreateItem = new ToolStripMenuItem("CREATE", null, (_, _) => Raise(ExplorerCommand.ScriptCreate));
        _scriptInsertItem = new ToolStripMenuItem("INSERT", null, (_, _) => Raise(ExplorerCommand.ScriptInsert));
        _scriptDropItem = new ToolStripMenuItem("DROP", null, (_, _) => Raise(ExplorerCommand.ScriptDrop));
        _scriptItem = new ToolStripMenuItem("Script as");
        _scriptItem.DropDownItems.AddRange([_scriptCreateItem, _scriptInsertItem, _scriptDropItem]);
        _newTableItem = new ToolStripMenuItem("New Table...", null, (_, _) => Raise(ExplorerCommand.NewTable));
        _newViewItem = new ToolStripMenuItem("New View...", null, (_, _) => Raise(ExplorerCommand.NewView));
        _newTriggerItem = new ToolStripMenuItem("New Trigger...", null, (_, _) => Raise(ExplorerCommand.NewTrigger));
        _importItem = new ToolStripMenuItem("Import Data...", null, (_, _) => Raise(ExplorerCommand.ImportData));
        _exportItem = new ToolStripMenuItem("Export Data...", null, (_, _) => Raise(ExplorerCommand.ExportData));
        _renameItem = new ToolStripMenuItem("Rename", null, (_, _) => BeginRename()) { ShortcutKeyDisplayString = "F2" };
        _dropItem = new ToolStripMenuItem("Drop...", null, (_, _) => Raise(ExplorerCommand.Drop)) { ShortcutKeyDisplayString = "Del" };
        _refreshItem = new ToolStripMenuItem("Refresh", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _closeItem = new ToolStripMenuItem("Close Database", null, (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty));
        _detachItem = new ToolStripMenuItem("Detach", null, (_, _) =>
        {
            if (_tree.SelectedNode?.Tag is AttachedNode attached)
            {
                CommandRequested?.Invoke(this, new ExplorerCommandEventArgs(ExplorerCommand.Detach, attached.Session, null, attached.Name));
            }
        });
        _menu = new ContextMenuStrip { Renderer = new RibbonRenderer() };
        _menu.Items.AddRange(
        [
            _openDataItem, _designItem, newQueryItem, _queryDesignItem, _scriptItem, new ToolStripSeparator(),
            _newTableItem, _newViewItem, _newTriggerItem, new ToolStripSeparator(),
            _importItem, _exportItem, new ToolStripSeparator(),
            _renameItem, _dropItem, new ToolStripSeparator(),
            _refreshItem, _closeItem, _detachItem,
        ]);
        _menu.Opening += (_, e) =>
        {
            if (_tree.SelectedNode is null || SelectedSession is null)
            {
                e.Cancel = true;
                return;
            }

            var obj = SelectedObject;
            var folder = _tree.SelectedNode.Tag as FolderNode;
            var isDatabase = _tree.SelectedNode.Tag is DatabaseNode;
            var readOnly = SelectedSession.IsReadOnly;
            _openDataItem.Visible = CanOpenData;
            _queryDesignItem.Visible = isDatabase || obj?.Type is SchemaObjectType.Table or SchemaObjectType.View;
            _designItem.Visible = obj is not null && !obj.IsSystem;
            _designItem.Enabled = !readOnly;
            _scriptItem.Visible = obj is not null;
            _scriptInsertItem.Enabled = obj?.Type == SchemaObjectType.Table;
            _newTableItem.Visible = isDatabase || folder?.Type == SchemaObjectType.Table;
            _newViewItem.Visible = isDatabase || folder?.Type == SchemaObjectType.View;
            _newTriggerItem.Visible = isDatabase || folder?.Type == SchemaObjectType.Trigger || obj?.Type == SchemaObjectType.Table;
            _newTableItem.Enabled = _newViewItem.Enabled = _newTriggerItem.Enabled = !readOnly;
            _importItem.Visible = isDatabase || folder?.Type == SchemaObjectType.Table || obj?.Type == SchemaObjectType.Table;
            _importItem.Enabled = !readOnly;
            _exportItem.Visible = isDatabase || obj?.Type is SchemaObjectType.Table or SchemaObjectType.View;
            _renameItem.Visible = _dropItem.Visible = obj is not null;
            _renameItem.Enabled = _dropItem.Enabled = CanRenameOrDrop;
            _closeItem.Visible = isDatabase;
            var isAttached = _tree.SelectedNode.Tag is AttachedNode;
            _detachItem.Visible = isAttached;
            _refreshItem.Visible = !isAttached;
            HideExtraSeparators(_menu.Items);
        };
        _tree.ContextMenuStrip = _menu;

        Controls.Add(_tree);
        Controls.Add(header);
    }

    private readonly ToolStripMenuItem _designItem;
    private readonly ToolStripMenuItem _queryDesignItem;
    private readonly ToolStripMenuItem _scriptItem;
    private readonly ToolStripMenuItem _scriptCreateItem;
    private readonly ToolStripMenuItem _scriptInsertItem;
    private readonly ToolStripMenuItem _scriptDropItem;
    private readonly ToolStripMenuItem _newTableItem;
    private readonly ToolStripMenuItem _newViewItem;
    private readonly ToolStripMenuItem _newTriggerItem;
    private readonly ToolStripMenuItem _importItem;
    private readonly ToolStripMenuItem _exportItem;
    private readonly ToolStripMenuItem _renameItem;
    private readonly ToolStripMenuItem _dropItem;
    private (TreeNode Node, string DisplayText)? _renaming;

    /// <summary>Raised for Design, Script as, New Table/View/Trigger, Rename, and Drop.</summary>
    public event EventHandler<ExplorerCommandEventArgs>? CommandRequested;

    /// <summary>The schema object at the selected node, if the node is one.</summary>
    public SchemaObject? SelectedObject => _tree.SelectedNode?.Tag is ObjectNode node ? node.Object : null;

    private bool CanRenameOrDrop => SelectedObject is { IsSystem: false } && SelectedSession is { IsReadOnly: false };

    private void Raise(ExplorerCommand command, string? newName = null)
    {
        if (SelectedSession is { } session)
        {
            CommandRequested?.Invoke(this, new ExplorerCommandEventArgs(command, session, SelectedObject, newName));
        }
    }

    /// <summary>Starts editing the selected object's name in the tree.</summary>
    private void BeginRename()
    {
        if (!CanRenameOrDrop || _tree.SelectedNode is not { } node)
        {
            return;
        }

        _renaming = (node, node.Text);
        node.Text = SelectedObject!.Name;
        _tree.LabelEdit = true;
        node.BeginEdit();
    }

    private void OnAfterLabelEdit(object? sender, NodeLabelEditEventArgs e)
    {
        if (_renaming is not { } renaming || e.Node != renaming.Node)
        {
            return;
        }

        // Keep the old text; the tree is refreshed after the rename runs.
        e.CancelEdit = true;
        renaming.Node.Text = renaming.DisplayText;
        _renaming = null;
        _tree.LabelEdit = false;
        var newName = e.Label?.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != SelectedObject?.Name)
        {
            Raise(ExplorerCommand.Rename, newName);
        }
    }

    /// <summary>Hides separators at the start, at the end, and next to each other. Uses Available, which works before the menu is shown.</summary>
    private static void HideExtraSeparators(ToolStripItemCollection items)
    {
        ToolStripItem? last = null;
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator separator)
            {
                separator.Available = last is not null and not ToolStripSeparator;
                if (separator.Available)
                {
                    last = separator;
                }
            }
            else if (item.Available)
            {
                last = item;
            }
        }

        if (last is ToolStripSeparator trailing)
        {
            trailing.Available = false;
        }
    }

    public event EventHandler? OpenDataRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? NewQueryRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler? SelectionChanged;

    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>The database that owns the selected node.</summary>
    public DatabaseSession? SelectedSession
    {
        get
        {
            for (var node = _tree.SelectedNode; node is not null; node = node.Parent)
            {
                if (node.Tag is DatabaseNode db)
                {
                    return db.Session;
                }
            }

            return null;
        }
    }

    /// <summary>The table or view under the selected node, if any.</summary>
    public SchemaObject? SelectedDataObject
    {
        get
        {
            for (var node = _tree.SelectedNode; node is not null; node = node.Parent)
            {
                if (node.Tag is ObjectNode { Object.Type: SchemaObjectType.Table or SchemaObjectType.View } obj)
                {
                    return obj.Object;
                }
            }

            return null;
        }
    }

    public bool CanOpenData => SelectedDataObject is not null;

    public async Task AddDatabaseAsync(DatabaseSession session)
    {
        var root = new TreeNode
        {
            Name = session.Id.ToString(),
            Text = RootText(session),
            Tag = new DatabaseNode(session),
            ToolTipText = session.FilePath,
        };
        SetIcon(root, session.IsReadOnly ? TreeIcon.DatabaseReadOnly : TreeIcon.Database);
        _tree.Nodes.Add(root);
        _tree.SelectedNode = root;
        await PopulateDatabaseAsync(root, session, expandedKeys: null);
        root.Expand();
    }

    public void RemoveDatabase(DatabaseSession session)
    {
        var root = FindRoot(session);
        root?.Remove();
    }

    public async Task RefreshDatabaseAsync(DatabaseSession session)
    {
        var root = FindRoot(session);
        if (root is null)
        {
            return;
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        CollectExpanded(root, expanded);
        await PopulateDatabaseAsync(root, session, expanded);
    }

    private TreeNode? FindRoot(DatabaseSession session) =>
        _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is DatabaseNode d && d.Session == session);

    private static string RootText(DatabaseSession session) =>
        session.IsReadOnly ? $"{session.DisplayName} (read-only)" : session.DisplayName;

    private async Task PopulateDatabaseAsync(TreeNode root, DatabaseSession session, HashSet<string>? expandedKeys)
    {
        IReadOnlyList<SchemaObject> objects;
        IReadOnlyList<AttachedDatabase> attached;
        try
        {
            objects = await new SchemaService(session).GetObjectsAsync();
            attached = await new AttachService(session).ListAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            return;
        }

        _tree.BeginUpdate();
        try
        {
            root.Nodes.Clear();
            AddFolder(root, session, SchemaObjectType.Table, "Tables", objects);
            AddFolder(root, session, SchemaObjectType.View, "Views", objects);
            AddFolder(root, session, SchemaObjectType.Index, "Indexes", objects);
            AddFolder(root, session, SchemaObjectType.Trigger, "Triggers", objects);
            if (attached.Count > 0)
            {
                var folder = new TreeNode($"Attached Databases ({attached.Count})") { Name = "folder:attached" };
                SetIcon(folder, TreeIcon.Folder);
                foreach (var database in attached)
                {
                    var node = new TreeNode($"{database.Name}  ({Path.GetFileName(database.FilePath)})")
                    {
                        Name = $"attached:{database.Name}",
                        Tag = new AttachedNode(session, database.Name),
                        ToolTipText = $"{database.FilePath}\nQuery its tables as {database.Name}.table_name",
                    };
                    SetIcon(node, TreeIcon.Database);
                    folder.Nodes.Add(node);
                }

                root.Nodes.Add(folder);
            }
        }
        finally
        {
            _tree.EndUpdate();
        }

        if (expandedKeys is not null)
        {
            RestoreExpanded(root, expandedKeys);
        }
    }

    private static void AddFolder(TreeNode root, DatabaseSession session, SchemaObjectType type, string title, IReadOnlyList<SchemaObject> all)
    {
        var items = all.Where(o => o.Type == type).ToList();
        var folder = new TreeNode($"{title} ({items.Count})")
        {
            Name = $"folder:{type}",
            Tag = new FolderNode(session, type),
        };
        SetIcon(folder, TreeIcon.Folder);

        foreach (var item in items)
        {
            var text = type is SchemaObjectType.Index or SchemaObjectType.Trigger
                ? $"{item.Name}  (on {item.TableName})"
                : item.Name;
            var node = new TreeNode(text)
            {
                Name = $"obj:{type}:{item.Name}",
                Tag = new ObjectNode(session, item),
                ToolTipText = item.Sql ?? string.Empty,
            };
            SetIcon(node, type switch
            {
                SchemaObjectType.Table => TreeIcon.Table,
                SchemaObjectType.View => TreeIcon.View,
                SchemaObjectType.Index => TreeIcon.Index,
                _ => TreeIcon.Trigger,
            });

            if (type is SchemaObjectType.Table or SchemaObjectType.View)
            {
                node.Nodes.Add(new TreeNode(PlaceholderText) { Tag = new PlaceholderNode() });
            }

            folder.Nodes.Add(node);
        }

        root.Nodes.Add(folder);
    }

    private async void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        var node = e.Node;
        if (node is null || node.Tag is not ObjectNode obj || node.Nodes.Count != 1 || node.Nodes[0].Tag is not PlaceholderNode)
        {
            return;
        }

        // Mark as loading so a second expand does not start another load.
        node.Nodes[0].Tag = null;
        try
        {
            var details = await new SchemaService(obj.Session).GetTableDetailsAsync(obj.Object.Name);
            FillTableNode(node, details);
        }
        catch (Exception ex)
        {
            node.Nodes.Clear();
            node.Nodes.Add(new TreeNode("(could not load)") { Tag = new PlaceholderNode() });
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void FillTableNode(TreeNode node, TableDetails details)
    {
        var fkColumns = details.ForeignKeys.SelectMany(f => f.FromColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _tree.BeginUpdate();
        try
        {
            node.Nodes.Clear();

            var columns = new TreeNode($"Columns ({details.Columns.Count})") { Name = "columns" };
            SetIcon(columns, TreeIcon.Folder);
            foreach (var column in details.Columns)
            {
                var flags = new List<string>();
                flags.Add(string.IsNullOrEmpty(column.DeclaredType) ? "ANY" : column.DeclaredType);
                if (column.IsPrimaryKey)
                {
                    flags.Add("PK");
                }

                if (fkColumns.Contains(column.Name))
                {
                    flags.Add("FK");
                }

                if (column.NotNull)
                {
                    flags.Add("NOT NULL");
                }

                if (column.IsGenerated)
                {
                    flags.Add("GENERATED");
                }

                var columnNode = new TreeNode($"{column.Name}  ({string.Join(", ", flags)})")
                {
                    ToolTipText = column.DefaultValue is null ? string.Empty : $"Default: {column.DefaultValue}",
                };
                SetIcon(columnNode, column.IsPrimaryKey ? TreeIcon.PrimaryKey : fkColumns.Contains(column.Name) ? TreeIcon.ForeignKey : TreeIcon.Column);
                columns.Nodes.Add(columnNode);
            }

            node.Nodes.Add(columns);

            if (details.Kind == TableKind.View)
            {
                return;
            }

            var keys = new TreeNode { Name = "keys" };
            SetIcon(keys, TreeIcon.Folder);
            if (details.PrimaryKey.Count > 0)
            {
                var pk = new TreeNode($"PRIMARY KEY ({string.Join(", ", details.PrimaryKey.Select(c => c.Name))})");
                SetIcon(pk, TreeIcon.PrimaryKey);
                keys.Nodes.Add(pk);
            }

            foreach (var fk in details.ForeignKeys)
            {
                var to = fk.ToColumns.All(c => c is null) ? "primary key" : string.Join(", ", fk.ToColumns.Select(c => c ?? "?"));
                var fkNode = new TreeNode($"FOREIGN KEY ({string.Join(", ", fk.FromColumns)}) → {fk.ReferencedTable} ({to})")
                {
                    ToolTipText = $"ON UPDATE {fk.OnUpdate}, ON DELETE {fk.OnDelete}",
                };
                SetIcon(fkNode, TreeIcon.ForeignKey);
                keys.Nodes.Add(fkNode);
            }

            keys.Text = $"Keys ({keys.Nodes.Count})";
            node.Nodes.Add(keys);

            var indexes = new TreeNode($"Indexes ({details.Indexes.Count})") { Name = "indexes" };
            SetIcon(indexes, TreeIcon.Folder);
            foreach (var index in details.Indexes)
            {
                var suffix = new List<string>();
                if (index.IsUnique)
                {
                    suffix.Add("UNIQUE");
                }

                if (index.IsPartial)
                {
                    suffix.Add("PARTIAL");
                }

                var columnsText = string.Join(", ", index.Columns.Select(c => c ?? "<expr>"));
                var text = $"{index.Name}  ({columnsText})" + (suffix.Count > 0 ? "  " + string.Join(" ", suffix) : string.Empty);
                var indexNode = new TreeNode(text);
                SetIcon(indexNode, TreeIcon.Index);
                indexes.Nodes.Add(indexNode);
            }

            node.Nodes.Add(indexes);
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private static void CollectExpanded(TreeNode node, HashSet<string> keys, string prefix = "")
    {
        foreach (TreeNode child in node.Nodes)
        {
            var key = prefix + "/" + child.Name;
            if (child.IsExpanded && !string.IsNullOrEmpty(child.Name))
            {
                keys.Add(key);
                CollectExpanded(child, keys, key);
            }
        }
    }

    private static void RestoreExpanded(TreeNode node, HashSet<string> keys, string prefix = "")
    {
        foreach (TreeNode child in node.Nodes)
        {
            var key = prefix + "/" + child.Name;
            if (keys.Contains(key))
            {
                child.Expand();
                RestoreExpanded(child, keys, key);
            }
        }
    }

    private static void SetIcon(TreeNode node, TreeIcon icon)
    {
        node.ImageKey = icon.ToString();
        node.SelectedImageKey = icon.ToString();
    }
}
