using System.Globalization;
using SqliteManager.App.Ui;

namespace SqliteManager.App.Panes;

/// <summary>Timestamped information and error messages.</summary>
internal sealed class MessagesView : UserControl
{
    private readonly RichTextBox _text;

    public MessagesView()
    {
        _text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.WindowBack,
            Font = new Font("Consolas", 9.5f),
            DetectUrls = false,
        };
        Controls.Add(_text);
    }

    public void Info(string message) => Append(message, SystemColors.WindowText);

    public void Error(string message) => Append(message, Theme.ErrorText);

    private void Append(string message, Color color)
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = color;
        _text.AppendText($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}] {message}{Environment.NewLine}");
        _text.SelectionColor = _text.ForeColor;
        _text.ScrollToCaret();
    }
}
