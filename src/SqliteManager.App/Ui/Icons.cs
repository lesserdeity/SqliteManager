using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SqliteManager.App.Ui;

/// <summary>Builds icons at run time so the app ships no image files.</summary>
internal static class Icons
{
    // Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10) share these code points.
    public const char NewDocument = '';
    public const char OpenFile = '';
    public const char Lock = '';
    public const char Close = '';
    public const char Refresh = '';
    public const char Grid = '';
    public const char Clear = '';
    public const char List = '';
    public const char Sidebar = '';
    public const char Panel = '';
    public const char CloseAll = '';
    public const char Exit = '';
    public const char Play = '';
    public const char PlaySelection = '';
    public const char PlayCurrent = '';
    public const char Stop = '';
    public const char Save = '';
    public const char SaveAs = '';
    public const char NewQuery = '';
    public const char Settings = '';
    public const char History = '';
    public const char Undo = '';
    public const char Add = '';
    public const char Delete = '';
    public const char Remove = '';
    public const char Photo = '';
    public const char Filter = '';
    public const char Table = '';
    public const char View = '';
    public const char Trigger = '';
    public const char Import = '';
    public const char Export = '';
    public const char Jobs = '';
    public const char Up = '';
    public const char Down = '';

    public const char QueryDesign = '';
    public const char Sigma = '';
    public const char Send = '';
    public const char Explain = '';
    public const char Repair = '';
    public const char Compact = '';
    public const char Analyze = '';
    public const char CheckMark = '';
    public const char Link = '';
    public const char Backup = '';
    public const char Attach = '';
    public const char Detach = '';
    public const char Brightness = '';
    public const char Tune = '';

    private static readonly string? GlyphFont = FindGlyphFont();

    private static string? FindGlyphFont()
    {
        using var fonts = new InstalledFontCollection();
        var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(names.Contains);
    }

    /// <summary>Draws an icon-font glyph. Falls back to a letter if no icon font is installed.</summary>
    public static Bitmap Glyph(char glyph, int size, Color color, string fallback = "?")
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var rect = new RectangleF(0, 0, size, size);
        if (GlyphFont is not null)
        {
            using var font = new Font(GlyphFont, size * 0.72f, GraphicsUnit.Pixel);
            g.DrawString(glyph.ToString(), font, brush, rect, format);
        }
        else
        {
            using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString(fallback, font, brush, rect, format);
        }

        return bitmap;
    }

    /// <summary>Small icons for the object explorer, drawn with GDI+.</summary>
    public static ImageList CreateTreeImages(int size)
    {
        var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
        foreach (var kind in Enum.GetValues<TreeIcon>())
        {
            list.Images.Add(kind.ToString(), DrawTreeIcon(kind, size));
        }

        return list;
    }

    private static Bitmap DrawTreeIcon(TreeIcon kind, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 16f, size / 16f);

        switch (kind)
        {
            case TreeIcon.Database:
            case TreeIcon.DatabaseReadOnly:
            {
                var fill = kind == TreeIcon.Database ? Color.FromArgb(0x3C, 0x7F, 0xC9) : Color.FromArgb(0x8A, 0x93, 0x9E);
                using var brush = new SolidBrush(fill);
                using var pen = new Pen(Color.FromArgb(0x24, 0x4F, 0x80), 1f);
                using var light = new SolidBrush(ControlPaint.Light(fill, 0.6f));
                g.FillRectangle(brush, 2.5f, 3.5f, 11f, 9f);
                g.FillEllipse(brush, 2.5f, 10.5f, 11f, 4f);
                g.FillEllipse(light, 2.5f, 1.5f, 11f, 4f);
                g.DrawEllipse(pen, 2.5f, 1.5f, 11f, 4f);
                g.DrawLine(pen, 2.5f, 3.5f, 2.5f, 12.5f);
                g.DrawLine(pen, 13.5f, 3.5f, 13.5f, 12.5f);
                g.DrawArc(pen, 2.5f, 10.5f, 11f, 4f, 0, 180);
                break;
            }

            case TreeIcon.Folder:
            {
                using var back = new SolidBrush(Color.FromArgb(0xE0, 0xB0, 0x4A));
                using var front = new SolidBrush(Color.FromArgb(0xF4, 0xCB, 0x6A));
                g.FillRectangle(back, 1, 3, 6, 3);
                g.FillRectangle(back, 1, 4, 14, 10);
                g.FillRectangle(front, 1, 6, 14, 8);
                break;
            }

            case TreeIcon.Table:
            case TreeIcon.View:
            {
                var header = kind == TreeIcon.Table ? Color.FromArgb(0x3C, 0x7F, 0xC9) : Color.FromArgb(0x3A, 0x9A, 0x6A);
                using var headerBrush = new SolidBrush(header);
                using var pen = new Pen(Color.FromArgb(0x7A, 0x86, 0x94), 1f);
                g.FillRectangle(Brushes.White, 1.5f, 2.5f, 13f, 11f);
                g.FillRectangle(headerBrush, 1.5f, 2.5f, 13f, 3f);
                g.DrawRectangle(pen, 1.5f, 2.5f, 13f, 11f);
                g.DrawLine(pen, 1.5f, 9.5f, 14.5f, 9.5f);
                g.DrawLine(pen, 6f, 5.5f, 6f, 13.5f);
                g.DrawLine(pen, 10.5f, 5.5f, 10.5f, 13.5f);
                break;
            }

            case TreeIcon.Column:
            {
                using var brush = new SolidBrush(Color.FromArgb(0x5B, 0x8F, 0xC8));
                using var pen = new Pen(Color.FromArgb(0x3A, 0x5F, 0x8A), 1f);
                g.FillRectangle(brush, 5.5f, 2.5f, 5f, 11f);
                g.DrawRectangle(pen, 5.5f, 2.5f, 5f, 11f);
                break;
            }

            case TreeIcon.PrimaryKey:
            case TreeIcon.ForeignKey:
            {
                var color = kind == TreeIcon.PrimaryKey ? Color.FromArgb(0xD8, 0xA0, 0x10) : Color.FromArgb(0x80, 0x88, 0x94);
                using var pen = new Pen(color, 2f);
                g.DrawEllipse(pen, 1.5f, 5f, 5.5f, 5.5f);
                g.DrawLine(pen, 7f, 7.75f, 14.5f, 7.75f);
                g.DrawLine(pen, 12.5f, 7.75f, 12.5f, 11f);
                g.DrawLine(pen, 10f, 7.75f, 10f, 10f);
                break;
            }

            case TreeIcon.Index:
            {
                using var pen = new Pen(Color.FromArgb(0x7A, 0x4F, 0xB0), 1.6f);
                g.DrawLine(pen, 2, 4, 14, 4);
                g.DrawLine(pen, 2, 8, 11, 8);
                g.DrawLine(pen, 2, 12, 8, 12);
                break;
            }

            case TreeIcon.Trigger:
            {
                using var brush = new SolidBrush(Color.FromArgb(0xE0, 0x7A, 0x1F));
                g.FillPolygon(brush, [new PointF(9, 1), new PointF(3, 9), new PointF(7.5f, 9), new PointF(6, 15), new PointF(13, 6.5f), new PointF(8.5f, 6.5f)]);
                break;
            }

            case TreeIcon.Info:
            {
                using var brush = new SolidBrush(Color.FromArgb(0xA0, 0xA8, 0xB2));
                g.FillEllipse(brush, 5, 5, 6, 6);
                break;
            }
        }

        return bitmap;
    }
}

internal enum TreeIcon
{
    Database,
    DatabaseReadOnly,
    Folder,
    Table,
    View,
    Column,
    PrimaryKey,
    ForeignKey,
    Index,
    Trigger,
    Info,
}
