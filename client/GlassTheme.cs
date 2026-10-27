using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Chispa.Launcher;

// A surface that can paint its own pixels. Children repaint these surfaces as their
// backdrop instead of relying on WinForms' transparent-background pass, which is skipped
// for every double-buffered control and therefore leaves recycled buffer content behind.
internal interface IGlassSurface
{
    void PaintSurface(Graphics graphics);
}

internal static class GlassTheme
{
    internal static readonly Color Background = Color.FromArgb(12, 20, 34);
    internal static readonly Color Surface = Color.FromArgb(27, 42, 59);
    internal static readonly Color Field = Color.FromArgb(22, 36, 53);
    internal static readonly Color Accent = Color.FromArgb(124, 239, 214);
    internal static readonly Color Muted = Color.FromArgb(164, 182, 201);
    internal static readonly Color Outline = Color.FromArgb(64, 88, 108);
    internal static readonly Image Mark = LoadImage("chispa-mark.png");
    internal static readonly Image Logo = LoadImage("chispa.png");

    private static Image LoadImage(string name)
    {
        using var stream = typeof(GlassTheme).Assembly.GetManifestResourceStream("Chispa.Brand." + name)!;
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    internal static Icon LoadIcon()
    {
        using var stream = typeof(GlassTheme).Assembly.GetManifestResourceStream("Chispa.Brand.chispa.ico")!;
        return new Icon(stream);
    }

    internal static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = MathF.Min(radius * 2, MathF.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Glow(Graphics g, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(brush, path);
    }

    // Paints the pixels that sit behind a control: each ancestor surface, outermost first.
    // The result always covers the control's whole client area, so a repaint can never
    // show stale pixels from a recycled buffer and the rounded corners blend into the
    // real backdrop instead of a black hole (the "black frame" with rough edges).
    internal static void PaintBackdrop(Control control, Graphics graphics)
    {
        if (control.Width <= 0 || control.Height <= 0) return;
        var ancestors = new List<Control>();
        for (var parent = control.Parent; parent is not null; parent = parent.Parent) ancestors.Add(parent);

        var shift = control.Location;
        var origins = new Point[ancestors.Count];
        for (var index = 0; index < ancestors.Count; index++)
        {
            origins[index] = new Point(-shift.X, -shift.Y);
            shift.Offset(ancestors[index].Left, ancestors[index].Top);
        }

        for (var index = ancestors.Count - 1; index >= 0; index--)
        {
            var ancestor = ancestors[index];
            var state = graphics.Save();
            try
            {
                graphics.TranslateTransform(origins[index].X, origins[index].Y);
                if (ancestor is IGlassSurface surface) surface.PaintSurface(graphics);
                else if (ancestor.BackColor.A == 255)
                {
                    using var fill = new SolidBrush(ancestor.BackColor);
                    graphics.FillRectangle(fill, ancestor.ClientRectangle);
                }
            }
            finally { graphics.Restore(state); }
        }

        if (ancestors.Count == 0)
        {
            using var fill = new SolidBrush(Background);
            graphics.FillRectangle(fill, control.ClientRectangle);
        }
    }

    // Let Windows use dark surfaces and rounded composition where available.
    internal static void ApplyWindowMaterial(IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var dark = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var corners = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var mica = 2;
            _ = DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

// Self-rendered container. The surface is cached and blitted for the control itself and
// for every child that needs to repaint its own backdrop.
internal abstract class GlassSurface : Panel, IGlassSurface
{
    private Bitmap? cached;
    private long cachedStamp = -1;
    private int revision;

    protected GlassSurface()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = GlassTheme.Background;
    }

    // Must paint every pixel of the given size: the backdrop plus this surface's own look.
    protected abstract void Render(Graphics graphics, Size size);

    internal void InvalidateSurface()
    {
        revision++;
        cached?.Dispose();
        cached = null;
        Invalidate(invalidateChildren: true);
    }

    public void PaintSurface(Graphics graphics)
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        var stamp = Stamp();
        if (cached is null || cached.Width != size.Width || cached.Height != size.Height || cachedStamp != stamp)
        {
            cached?.Dispose();
            cached = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var canvas = Graphics.FromImage(cached);
            canvas.SetClip(new Rectangle(Point.Empty, size));
            Render(canvas, size);
            cachedStamp = stamp;
        }
        graphics.DrawImageUnscaled(cached, 0, 0);
    }

    private long Stamp()
    {
        long stamp = revision;
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            if (parent is GlassSurface surface) stamp = unchecked(stamp * 31 + surface.revision);
        return stamp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintSurface(e.Graphics);
        base.OnPaint(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        InvalidateSurface();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        InvalidateSurface();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { cached?.Dispose(); cached = null; }
        base.Dispose(disposing);
    }
}

internal sealed class GlassCanvas : GlassSurface
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsContentLayer { get; set; }
    // Page art is rendered into the cached surface, so children inherit it when they
    // repaint their own backdrop instead of erasing it.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<Graphics, float>? Decorations { get; set; }

    protected override void Render(Graphics graphics, Size size)
    {
        if (IsContentLayer)
        {
            GlassTheme.PaintBackdrop(this, graphics);
            var state = graphics.Save();
            try { Decorations?.Invoke(graphics, DeviceDpi / 96f); }
            finally { graphics.Restore(state); }
            return;
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Point.Empty, size);
        using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(16, 30, 47), GlassTheme.Background, 40f))
            graphics.FillRectangle(fill, bounds);
        GlassTheme.Glow(graphics, new Rectangle(-size.Width / 5, -size.Height / 3, size.Width, size.Height * 3 / 2), Color.FromArgb(75, 39, 137, 144));
        GlassTheme.Glow(graphics, new Rectangle(size.Width / 2, size.Height / 4, size.Width * 3 / 4, size.Height), Color.FromArgb(65, 83, 73, 148));
        var decorationState = graphics.Save();
        try { Decorations?.Invoke(graphics, DeviceDpi / 96f); }
        finally { graphics.Restore(decorationState); }
        using var frame = GlassTheme.Rounded(new RectangleF(0.5f, 0.5f,
            Math.Max(1, size.Width - 1f), Math.Max(1, size.Height - 1f)), 20 * DeviceDpi / 96f);
        using var line = new Pen(Color.FromArgb(75, 158, 208, 225), 1f);
        graphics.DrawPath(line, frame);
    }
}

internal sealed class GlassLayer : GlassSurface
{
    protected override void Render(Graphics graphics, Size size) => GlassTheme.PaintBackdrop(this, graphics);
}

internal sealed class ChromeButton : Control
{
    private readonly bool close;
    private bool hovered;
    private bool pressed;

    internal ChromeButton(bool close)
    {
        this.close = close;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.StandardClick, true);
        BackColor = GlassTheme.Background;
        ForeColor = Color.FromArgb(194, 219, 229);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = close ? "关闭加载器" : "最小化加载器";
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        GlassTheme.PaintBackdrop(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        if (hovered || pressed || Focused)
        {
            using var background = new SolidBrush(close
                ? Color.FromArgb(pressed ? 180 : 125, 208, 80, 94)
                : Color.FromArgb(pressed ? 66 : 42, 178, 220, 228));
            using var shape = GlassTheme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 7 * scale);
            g.FillPath(background, shape);
        }
        using var glyph = new Pen(ForeColor, 1.7f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = Width / 2f;
        var y = Height / 2f;
        if (close)
        {
            g.DrawLine(glyph, x - 5 * scale, y - 5 * scale, x + 5 * scale, y + 5 * scale);
            g.DrawLine(glyph, x + 5 * scale, y - 5 * scale, x - 5 * scale, y + 5 * scale);
        }
        else g.DrawLine(glyph, x - 6 * scale, y + 3 * scale, x + 6 * scale, y + 3 * scale);
        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 2, Width - 6, Height - 4), ForeColor, BackColor);
        base.OnPaint(e);
    }
}

internal sealed class GlassPanel : GlassSurface
{
    protected override void Render(Graphics graphics, Size size)
    {
        GlassTheme.PaintBackdrop(this, graphics);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var bounds = new RectangleF(0.5f, 0.5f, MathF.Max(size.Width - 1f, 1f), MathF.Max(size.Height - 1f, 1f));
        using var path = GlassTheme.Rounded(bounds, 24 * scale);
        using var fill = new LinearGradientBrush(bounds, Color.FromArgb(39, 175, 209, 226), Color.FromArgb(16, 120, 144, 191), 65f);
        graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(52, 211, 236, 255));
        graphics.DrawPath(border, path);
    }
}

// Rounded text field container: paints its own backdrop so the border stays crisp.
internal sealed class GlassField : GlassSurface
{
    private bool highlighted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Highlighted
    {
        get => highlighted;
        set
        {
            if (highlighted == value) return;
            highlighted = value;
            InvalidateSurface();
        }
    }

    protected override void Render(Graphics graphics, Size size)
    {
        GlassTheme.PaintBackdrop(this, graphics);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var bounds = new RectangleF(0.5f, 0.5f, MathF.Max(size.Width - 1f, 1f), MathF.Max(size.Height - 1f, 1f));
        using var path = GlassTheme.Rounded(bounds, 10 * scale);
        using (var fill = new SolidBrush(GlassTheme.Field)) graphics.FillPath(fill, path);
        using var pen = new Pen(highlighted ? GlassTheme.Accent : GlassTheme.Outline);
        graphics.DrawPath(pen, path);
    }
}

internal sealed class GlassButton : Button
{
    private bool primary;
    private bool hover;
    private bool pressed;

    public GlassButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = GlassTheme.Background;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary
    {
        get => primary;
        set
        {
            if (primary == value) return;
            primary = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        GlassTheme.PaintBackdrop(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var bounds = new RectangleF(0.5f, 0.5f, MathF.Max(Width - 1f, 1f), MathF.Max(Height - 1f, 1f));
        using var path = GlassTheme.Rounded(bounds, 12 * scale);

        Color top, bottom;
        if (!Enabled)
        {
            top = Color.FromArgb(40, 56, 73);
            bottom = top;
        }
        else if (Primary)
        {
            top = pressed ? Color.FromArgb(104, 224, 202) : hover ? Color.FromArgb(168, 255, 234) : GlassTheme.Accent;
            bottom = pressed ? Color.FromArgb(80, 186, 186) : Color.FromArgb(96, 209, 209);
        }
        else
        {
            top = Color.FromArgb(pressed ? 96 : hover ? 75 : 35, 171, 200, 224);
            bottom = top;
        }

        using (var fill = new LinearGradientBrush(bounds, top, bottom, 25f)) g.FillPath(fill, path);
        using (var border = new Pen(Color.FromArgb(Focused ? 220 : 55, 202, 234, 255))) g.DrawPath(border, path);

        var textColor = !Enabled ? Color.FromArgb(134, 153, 171) : Primary ? GlassTheme.Background : Color.FromArgb(232, 244, 255);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Round(RectangleF.Inflate(bounds, -5 * scale, -5 * scale)));
    }
}

internal sealed class GlassCheck : CheckBox
{
    public GlassCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = GlassTheme.Background;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        GlassTheme.PaintBackdrop(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var side = 15 * scale;
        var box = new RectangleF(0.5f, (Height - side) / 2f, side, side);
        using var path = GlassTheme.Rounded(box, 4 * scale);
        using (var fill = new SolidBrush(Checked ? GlassTheme.Accent : GlassTheme.Field)) g.FillPath(fill, path);
        using (var pen = new Pen(Checked ? GlassTheme.Accent : Color.FromArgb(86, 108, 128))) g.DrawPath(pen, path);
        if (Checked)
        {
            using var glyph = new Font("Segoe UI", 12 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "✓", glyph, Rectangle.Round(box), GlassTheme.Background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        var textBounds = Rectangle.FromLTRB((int)MathF.Ceiling(side + 9 * scale), 0, Width, Height);
        TextRenderer.DrawText(g, Text, Font, textBounds, Enabled ? ForeColor : Color.FromArgb(112, 130, 148),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, ClientRectangle, ForeColor, BackColor);
    }
}

internal sealed class GlassProgress : Control
{
    private int value;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } }

    public GlassProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = GlassTheme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        GlassTheme.PaintBackdrop(this, g);
        using (var track = new SolidBrush(Color.FromArgb(34, 52, 71))) g.FillRectangle(track, ClientRectangle);
        if (value <= 0) return;
        var width = Math.Max(Width * value / 100, 1);
        using var fill = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(Width, 1), Math.Max(Height, 1)),
            GlassTheme.Accent, Color.FromArgb(124, 171, 246), 0f);
        g.FillRectangle(fill, 0, 0, width, Height);
    }
}
