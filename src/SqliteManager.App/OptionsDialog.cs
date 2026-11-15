using SqliteManager.App.Ui;
using SqliteManager.Core.Settings;

namespace SqliteManager.App;

/// <summary>File > Options: application settings.</summary>
internal sealed class OptionsDialog : Form
{
    private readonly NumericUpDown _rowLimit;
    private readonly ComboBox _theme;

    public OptionsDialog(AppSettings settings)
    {
        Text = "Options";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var heading = new Label
        {
            Text = "Query editor",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = Theme.Accent,
            Margin = new Padding(0, 0, 0, 8),
        };

        var label = new Label
        {
            Text = "Maximum rows per result set:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0),
        };

        _rowLimit = new NumericUpDown
        {
            Minimum = AppSettings.MinQueryRowLimit,
            Maximum = AppSettings.MaxQueryRowLimit,
            Value = Math.Clamp(settings.QueryRowLimit, AppSettings.MinQueryRowLimit, AppSettings.MaxQueryRowLimit),
            ThousandsSeparator = true,
            Increment = 10_000,
            Width = 120,
        };

        var hint = new Label
        {
            Text = $"Rows after this limit are not loaded. Large limits use more memory.\nDefault: {AppSettings.DefaultQueryRowLimit:N0}.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 12),
        };

        var themeHeading = new Label
        {
            Text = "Appearance",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = Theme.Accent,
            Margin = new Padding(0, 0, 0, 8),
        };
        var themeLabel = new Label { Text = "Theme:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        _theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _theme.Items.AddRange(["Light", "Dark"]);
        _theme.SelectedIndex = settings.IsDarkTheme ? 1 : 0;
        var themeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        themeRow.Controls.AddRange([themeLabel, _theme]);
        var themeHint = new Label
        {
            Text = "A new theme is used after SQLite Manager restarts.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 12),
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        AcceptButton = ok;
        CancelButton = cancel;

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange([label, _rowLimit]);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(heading);
        layout.Controls.Add(row);
        layout.Controls.Add(hint);
        layout.Controls.Add(themeHeading);
        layout.Controls.Add(themeRow);
        layout.Controls.Add(themeHint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public int QueryRowLimit => (int)_rowLimit.Value;

    public string ThemeName => _theme.SelectedIndex == 1 ? AppSettings.DarkTheme : AppSettings.LightTheme;
}
