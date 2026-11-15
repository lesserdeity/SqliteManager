namespace SqliteManager.App.Ui;

/// <summary>
/// Office-style colors used across the application, in a light and a dark variant.
/// The theme is chosen once at startup (<see cref="Initialize"/>); changing it takes effect after a restart.
/// </summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Initialize(bool dark) => IsDark = dark;

    private static Color Pick(int light, int dark) => Color.FromArgb(unchecked((int)0xFF000000) | (IsDark ? dark : light));

    public static Color Accent => Pick(0x1F5FA8, 0x2B5C94);
    public static Color AccentDark => Pick(0x174A85, 0x1E4570);
    public static Color AccentHover => Pick(0x3A77BD, 0x3A6EA8);
    public static Color AccentText => Color.White;

    /// <summary>Accent-colored text on a body (light or dark) background.</summary>
    public static Color AccentOnBody => Pick(0x1F5FA8, 0x8AB4E8);

    public static Color RibbonBody => Pick(0xF7F8FA, 0x2B2B2E);
    public static Color RibbonBorder => Pick(0xD5D9DF, 0x45454A);
    public static Color RibbonGroupText => Pick(0x606670, 0xA8ACB2);
    public static Color RibbonSeparator => Pick(0xDDE1E6, 0x45454A);

    public static Color ButtonHover => Pick(0xDCE8F6, 0x3E4550);
    public static Color ButtonHoverBorder => Pick(0xB5CDEB, 0x56606E);
    public static Color ButtonPressed => Pick(0xC3D7EF, 0x264F78);
    public static Color ButtonChecked => Pick(0xCFDFF3, 0x2F4A66);

    public static Color PaneHeader => Pick(0xE9EEF5, 0x2D2D30);
    public static Color PaneHeaderText => Pick(0x2B333D, 0xE0E0E0);
    public static Color WorkspaceBack => Pick(0xE4E8ED, 0x1E1E1E);

    /// <summary>Background and text of content areas (grids, lists, editors).</summary>
    public static Color WindowBack => Pick(0xFFFFFF, 0x1E1E1E);
    public static Color WindowText => Pick(0x1E1E1E, 0xDCDCDC);
    public static Color GridLines => Pick(0xE1E4E8, 0x3A3A3E);
    public static Color SelectionText => Pick(0x000000, 0xFFFFFF);

    public static Color NullText => Pick(0x9A9A9A, 0x858585);
    public static Color MutedText => Pick(0x7A828C, 0x8E949C);
    public static Color ErrorText => Pick(0xC42B1C, 0xF48771);
    public static Color WarningText => Pick(0xC27C0E, 0xE0A040);
    public static Color SuccessText => Pick(0x2E8B3E, 0x6CC47A);
    public static Color GlyphColor => Pick(0x2F3A48, 0xD0D4DA);

    // Query designer canvas.
    public static Color CanvasBack => Pick(0xF3F5F8, 0x252526);
    public static Color BoxBack => Pick(0xFFFFFF, 0x2D2D30);
    public static Color BoxText => Pick(0x1E1E1E, 0xDCDCDC);
    public static Color JoinLine => Pick(0x2F3A48, 0x9CB4D0);

    // Pending edits in data grids.
    public static Color ChangedBack => Pick(0xFFF2BF, 0x4A4220);
    public static Color InsertedBack => Pick(0xE6F4E6, 0x1F3A24);
    public static Color DeletedBack => Pick(0xF2F2F2, 0x2A2A2A);
}
