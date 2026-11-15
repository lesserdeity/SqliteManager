using ScintillaNET;
using SqliteManager.App.Ui;
using SqliteManager.Core.Query;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.App.Documents;

/// <summary>Scintilla editor set up for SQLite: highlighting, line numbers, folding, brace matching, autocomplete.</summary>
internal sealed class SqlEditor : Scintilla
{
    private const int FoldMargin = 2;

    public SqlEditor()
    {
        BorderStyle = ScintillaNET.BorderStyle.None;
        LexerName = "sql";

        StyleResetDefault();
        Styles[Style.Default].Font = "Consolas";
        Styles[Style.Default].Size = 10;
        var dark = Theme.IsDark;
        static Color C(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | rgb);
        Styles[Style.Default].ForeColor = Theme.WindowText;
        Styles[Style.Default].BackColor = Theme.WindowBack;
        StyleClearAll();

        var comment = C(dark ? 0x6A9955 : 0x008000);
        Styles[Style.Sql.Comment].ForeColor = comment;
        Styles[Style.Sql.CommentLine].ForeColor = comment;
        Styles[Style.Sql.CommentDoc].ForeColor = comment;
        Styles[Style.Sql.CommentLineDoc].ForeColor = comment;
        Styles[Style.Sql.Number].ForeColor = C(dark ? 0xB5CEA8 : 0x098658);
        Styles[Style.Sql.Word].ForeColor = C(dark ? 0x569CD6 : 0x0000FF);
        Styles[Style.Sql.Word2].ForeColor = C(dark ? 0x4EC9B0 : 0x267F99);
        Styles[Style.Sql.User1].ForeColor = C(dark ? 0xDCDCAA : 0xAF00DB);
        Styles[Style.Sql.Character].ForeColor = C(dark ? 0xCE9178 : 0xA31515); // 'string'
        Styles[Style.Sql.String].ForeColor = C(dark ? 0xC586C0 : 0x6F42C1); // "quoted name"
        Styles[Style.Sql.QuotedIdentifier].ForeColor = C(dark ? 0xC586C0 : 0x6F42C1); // `quoted name`
        Styles[Style.Sql.Operator].ForeColor = C(dark ? 0xB4B4B4 : 0x555555);
        Styles[Style.LineNumber].ForeColor = C(dark ? 0x858585 : 0x237E93);
        Styles[Style.LineNumber].BackColor = Theme.RibbonBody;
        Styles[Style.BraceLight].BackColor = C(dark ? 0x264F78 : 0xC8E1FF);
        Styles[Style.BraceLight].Bold = true;
        Styles[Style.BraceBad].ForeColor = Color.Red;

        // Lexilla's SQL lexer matches keywords in lower case.
        SetKeywords(0, string.Join(" ", SqlKeywords.All).ToLowerInvariant());
        SetKeywords(1, string.Join(" ", SqlKeywords.TypeNames).ToLowerInvariant());
        SetKeywords(4, string.Join(" ", SqlKeywords.BuiltInFunctions).ToLowerInvariant());
        SetProperty("lexer.sql.backticks.identifier", "1");
        SetProperty("fold", "1");
        SetProperty("fold.compact", "0");
        SetProperty("fold.comment", "1");

        CaretLineBackColor = C(dark ? 0x2A2D2E : 0xF3F7FC);
        CaretForeColor = Theme.WindowText;
        SelectionBackColor = C(dark ? 0x264F78 : 0xADD6FF);
        TabWidth = 4;
        UseTabs = false;
        IndentationGuides = IndentView.LookBoth;

        Margins[0].Type = MarginType.Number;
        Margins[0].Width = TextWidth(Style.LineNumber, "99999");
        Margins[1].Width = 0;

        Margins[FoldMargin].Type = MarginType.Symbol;
        Margins[FoldMargin].Mask = Marker.MaskFolders;
        Margins[FoldMargin].Sensitive = true;
        Margins[FoldMargin].Width = 16;
        SetFoldMarginColor(true, Theme.RibbonBody);
        SetFoldMarginHighlightColor(true, Theme.RibbonBody);
        for (var i = Marker.FolderEnd; i <= Marker.FolderOpen; i++)
        {
            Markers[i].SetForeColor(Theme.WindowBack);
            Markers[i].SetBackColor(C(0x808894));
        }

        Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;
        Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;
        Markers[Marker.FolderEnd].Symbol = MarkerSymbol.BoxPlusConnected;
        Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
        Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.BoxMinusConnected;
        Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
        Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;
        AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;

        AutoCIgnoreCase = true;
        AutoCOrder = Order.PerformSort;
        AutoCSeparator = '\n';
        AutoCMaxHeight = 12;
        AutoCAutoHide = true;

        CharAdded += OnCharAdded;
        UpdateUI += OnUpdateUI;
    }

    /// <summary>Names from the live schema. Replaced when the schema changes.</summary>
    public SchemaSnapshot Schema { get; private set; } = SchemaSnapshot.Empty;

    public void SetSchema(SchemaSnapshot schema)
    {
        Schema = schema;
        var functions = schema.Functions.Count > 0 ? schema.Functions : SqlKeywords.BuiltInFunctions;
        SetKeywords(4, string.Join(" ", functions).ToLowerInvariant());
        Colorize(0, -1);
    }

    /// <summary>Shows the autocomplete list for the word at the caret.</summary>
    public void ShowCompletion(bool explicitRequest)
    {
        var caret = CurrentPosition;
        var result = CompletionProvider.Complete(Text, caret, Schema, explicitRequest);
        if (result.Items.Count == 0)
        {
            if (AutoCActive)
            {
                AutoCCancel();
            }

            return;
        }

        // Do not pop up a list whose only entry is the word already typed.
        if (!explicitRequest && result.Items.Count == 1
            && string.Equals(result.Items[0].InsertText, result.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AutoCShow(caret - result.WordStart, string.Join("\n", result.Items.Select(i => i.InsertText).Distinct()));
    }

    /// <summary>Moves the caret to the start of a 1-based line and scrolls it into view.</summary>
    public void GoToLine(int line)
    {
        var index = Math.Clamp(line - 1, 0, Math.Max(0, Lines.Count - 1));
        Lines[index].Goto();
        Focus();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        // Let the editor receive Tab instead of moving focus, and Esc while a popup is open
        // (so closing autocomplete does not also close the dialog the editor is in).
        var key = keyData & Keys.KeyCode;
        return key == Keys.Tab
            || (key == Keys.Escape && (AutoCActive || CallTipActive))
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Space)
        {
            e.SuppressKeyPress = true;
            ShowCompletion(explicitRequest: true);
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        var c = (char)e.Char;
        if (c == '.')
        {
            ShowCompletion(explicitRequest: false);
            return;
        }

        if (SqlScriptSplitter.IsIdentifierPart(c) && !AutoCActive)
        {
            // Open after two identifier characters.
            var start = WordStartPosition(CurrentPosition, true);
            if (CurrentPosition - start >= 2 || (start > 0 && GetCharAt(start - 1) == '.'))
            {
                ShowCompletion(explicitRequest: false);
            }
        }
    }

    private void OnUpdateUI(object? sender, UpdateUIEventArgs e)
    {
        var caret = CurrentPosition;
        var bracePos = -1;
        if (caret > 0 && IsBrace(GetCharAt(caret - 1)))
        {
            bracePos = caret - 1;
        }
        else if (IsBrace(GetCharAt(caret)))
        {
            bracePos = caret;
        }

        if (bracePos < 0)
        {
            BraceHighlight(InvalidPosition, InvalidPosition);
            return;
        }

        var match = BraceMatch(bracePos);
        if (match == InvalidPosition)
        {
            BraceBadLight(bracePos);
        }
        else
        {
            BraceHighlight(bracePos, match);
        }
    }

    private static bool IsBrace(int c) => c is '(' or ')';
}
