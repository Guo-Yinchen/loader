using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Chispa.Launcher;

internal sealed class ProgramTile : Control
{
    private readonly Image? icon;
    public ProgramInfo Program { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsSelected { get; set; }
    private bool hover;

    public ProgramTile(ProgramInfo program)
    {
        Program = program;
        Size = new Size(272, 158);
        Margin = new Padding(0, 0, 16, 16);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = program.Name;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = GlassTheme.Background;
        AccessibleRole = AccessibleRole.PushButton;
        if (program.Icon?.StartsWith("data:image/png;base64,", StringComparison.Ordinal) == true)
        {
            try
            {
                var bytes = Convert.FromBase64String(program.Icon["data:image/png;base64,".Length..]);
                if (bytes.Length <= 128 * 1024)
                {
                    using var source = new MemoryStream(bytes);
                    using var loaded = Image.FromStream(source);
                    if (loaded.Width <= 512 && loaded.Height <= 512) icon = new Bitmap(loaded);
                }
            }
            catch (Exception) { }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Erase first: a double-buffered control never gets a background pass, so the tile
        // must repaint the page behind it or previously drawn text stays on screen.
        GlassTheme.PaintBackdrop(this, g);
        var scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        var width = (int)(Width / scale);
        var height = (int)(Height / scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, MathF.Max(width - 1f, 1f), MathF.Max(height - 1f, 1f));
        using var shape = GlassTheme.Rounded(bounds, 20);
        using var fill = new LinearGradientBrush(bounds,
            IsSelected ? Color.FromArgb(47, 76, 84) : Color.FromArgb(hover ? 51 : 36, 52, 71),
            Color.FromArgb(24, 36, 53), 95f);
        g.FillPath(fill, shape);
        using var border = new Pen(IsSelected || Focused ? GlassTheme.Accent : Color.FromArgb(61, 82, 104), IsSelected ? 1.5f : 1f);
        g.DrawPath(border, shape);

        var iconBounds = new Rectangle(19, 21, 52, 52);
        using var iconShape = GlassTheme.Rounded(iconBounds, 12);
        using var iconFill = new LinearGradientBrush(iconBounds,
            Program.IsBeta ? Color.FromArgb(114, 79, 156) : Color.FromArgb(57, 126, 132),
            Color.FromArgb(30, 49, 70), 45f);
        g.FillPath(iconFill, iconShape);
        if (icon is not null)
        {
            using var previous = g.Clip;
            g.SetClip(iconShape);
            g.DrawImage(icon, iconBounds);
            g.Clip = previous;
        }
        else
        {
            var mark = Program.Icon is { Length: > 0 } text &&
                !text.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                ? text : Program.Name[..1].ToUpperInvariant();
            using var font = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(mark, font, brush, iconBounds, centered);
        }

        using var titleFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(244, 247, 248));
        using var muted = new SolidBrush(GlassTheme.Muted);
        using var accent = new SolidBrush(Program.IsBeta ? Color.FromArgb(209, 183, 251) : GlassTheme.Accent);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        g.DrawString(Program.Name, titleFont, white, new Rectangle(86, 20, width - 110, 31), format);
        var category = Program.IsBeta ? "BETA ACCESS" : "STABLE";
        using var tagFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(category, tagFont, accent, new Rectangle(87, 53, width - 112, 21), format);
        using var divider = new Pen(Color.FromArgb(53, 77, 98));
        g.DrawLine(divider, 20, 100, width - 21, 100);
        using var stateFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString(Program.HasRelease != false ? "可下载 / 更新" : "即将推出 · 暂无版本", stateFont, muted, new Rectangle(20, 116, width - 58, 25), format);
        g.DrawString(IsSelected ? "✓" : "→", stateFont, accent, new Rectangle(width - 38, 116, 22, 25), format);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) icon?.Dispose();
        base.Dispose(disposing);
    }
}
