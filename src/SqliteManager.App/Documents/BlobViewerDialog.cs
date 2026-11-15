using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SqliteManager.App.Ui;
using SqliteManager.Core.Data;

namespace SqliteManager.App.Documents;

internal enum BlobEditAction
{
    None,
    SetNull,
    SetBytes,
}

/// <summary>Shows a cell value as hex, text, or an image. Can load the value from a file, save it to a file, or set it to NULL.</summary>
internal sealed class BlobViewerDialog : Form
{
    private const int MaxTextPreview = 2_000_000;

    private readonly bool _editable;
    private readonly ListView _hex;
    private readonly TextBox _text;
    private readonly ComboBox _encoding;
    private readonly PictureBox _picture;
    private readonly Label _imageMessage;
    private readonly Label _info;
    private readonly Button _save;
    private byte[]? _data;
    private bool _isNull;

    public BlobViewerDialog(string title, object? value, bool editable)
    {
        _editable = editable;
        Text = title;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(540));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(320));

        _hex = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            Font = new Font("Consolas", 10f),
            BorderStyle = BorderStyle.None,
        };
        _hex.Columns.Add("Hex", LogicalToDeviceUnits(700));
        _hex.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem(BlobService.FormatHexLine(_data ?? [], e.ItemIndex));

        _encoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        _encoding.Items.AddRange(["UTF-8", "UTF-16 (little endian)", "UTF-16 (big endian)", "Latin-1"]);
        _encoding.SelectedIndex = 0;
        _encoding.SelectedIndexChanged += (_, _) => ShowText();
        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            BackColor = Theme.WindowBack,
            BorderStyle = BorderStyle.None,
        };

        _picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.WindowBack };
        _imageMessage = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.RibbonGroupText };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var hexPage = new TabPage("Hex");
        hexPage.Controls.Add(_hex);
        var textPage = new TabPage("Text");
        textPage.Controls.Add(_text);
        textPage.Controls.Add(_encoding);
        var imagePage = new TabPage("Image");
        imagePage.Controls.Add(_picture);
        imagePage.Controls.Add(_imageMessage);
        tabs.TabPages.AddRange([hexPage, textPage, imagePage]);

        _info = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) };
        var load = new Button { Text = "Load from File...", AutoSize = true, Enabled = editable };
        _save = new Button { Text = "Save to File...", AutoSize = true };
        var setNull = new Button { Text = "Set to NULL", AutoSize = true, Enabled = editable };
        var ok = new Button { Text = editable ? "OK" : "Close", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Visible = editable };
        load.Click += async (_, _) => await LoadFromFileAsync();
        _save.Click += async (_, _) => await SaveToFileAsync();
        setNull.Click += (_, _) =>
        {
            Show(null);
            Action = BlobEditAction.SetNull;
        };
        AcceptButton = ok;
        CancelButton = editable ? cancel : ok;

        var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Left, WrapContents = false };
        left.Controls.AddRange([_info, load, _save, setNull]);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        right.Controls.AddRange([cancel, ok]);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(42), Padding = new Padding(8, 6, 8, 6) };
        bottom.Controls.Add(left);
        bottom.Controls.Add(right);

        Controls.Add(tabs);
        Controls.Add(bottom);

        Show(value);
        if (BlobService.DetectImage(_data) != BlobImageFormat.None)
        {
            tabs.SelectedTab = imagePage;
        }
        else if (value is string)
        {
            tabs.SelectedTab = textPage;
        }
    }

    /// <summary>What to do with the cell when the dialog returns OK.</summary>
    public BlobEditAction Action { get; private set; }

    /// <summary>The new value when <see cref="Action"/> is <see cref="BlobEditAction.SetBytes"/>.</summary>
    public byte[]? NewBytes => Action == BlobEditAction.SetBytes ? _data : null;

    private void Show(object? value)
    {
        _isNull = value is null;
        _data = value switch
        {
            null => null,
            byte[] bytes => bytes,
            _ => Encoding.UTF8.GetBytes(ValueParser.FormatForEdit(value)),
        };

        var length = _data?.Length ?? 0;
        _info.Text = _isNull ? "NULL" : $"{length:N0} bytes" + (value is byte[] ? string.Empty : " (shown as UTF-8 text)");
        _save.Enabled = !_isNull;
        _hex.VirtualListSize = BlobService.HexLineCount(length);
        _hex.Invalidate();
        ShowText();
        ShowImage();
    }

    private void ShowText()
    {
        if (_data is null)
        {
            _text.Text = _isNull ? "NULL" : string.Empty;
            return;
        }

        var encoding = _encoding.SelectedIndex switch
        {
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.Latin1,
            _ => Encoding.UTF8,
        };
        var count = Math.Min(_data.Length, MaxTextPreview);
        var text = encoding.GetString(_data, 0, count).Replace("\0", "\\0", StringComparison.Ordinal);
        if (count < _data.Length)
        {
            text += $"{Environment.NewLine}… (first {count:N0} of {_data.Length:N0} bytes shown)";
        }

        _text.Text = text.ReplaceLineEndings(Environment.NewLine);
    }

    private void ShowImage()
    {
        var old = _picture.Image;
        _picture.Image = null;
        old?.Dispose();

        var format = BlobService.DetectImage(_data);
        if (_data is null || format == BlobImageFormat.None)
        {
            _imageMessage.Text = "This value is not an image (PNG, JPEG, GIF, BMP, ICO, or WebP).";
            _imageMessage.BringToFront();
            return;
        }

        try
        {
            using var stream = new MemoryStream(_data);
            using var image = Image.FromStream(stream);
            _picture.Image = new Bitmap(image);
            _picture.BringToFront();
            _info.Text += string.Create(CultureInfo.CurrentCulture, $"  ·  {format} {image.Width}×{image.Height}");
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            _imageMessage.Text = $"Looks like a {format} image, but Windows cannot display it.";
            _imageMessage.BringToFront();
        }
    }

    private async Task LoadFromFileAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Load BLOB from File", Filter = "All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var bytes = await BlobService.ReadFileAsync(dialog.FileName);
            Show(bytes);
            Action = BlobEditAction.SetBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Load from File", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveToFileAsync()
    {
        if (_data is null)
        {
            return;
        }

        var extension = BlobService.SuggestedExtension(BlobService.DetectImage(_data));
        using var dialog = new SaveFileDialog
        {
            Title = "Save BLOB to File",
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} file (*{extension})|*{extension}|All files (*.*)|*.*",
            FileName = "blob" + extension,
            OverwritePrompt = true, // Asks before replacing an existing file.
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            await BlobService.WriteFileAsync(dialog.FileName, _data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Save to File", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
