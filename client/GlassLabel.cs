using System.ComponentModel;

namespace Chispa.Launcher;

// Keep text and its cached glass backdrop in the same buffer, including partial repaints.
// Measurement and painting share the same padding and GDI flags. TextBoxControl omits an
// incomplete last line rather than showing half a glyph at the bottom of the control.
internal sealed class GlassLabel : Label
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
        TextFormatFlags.TextBoxControl | TextFormatFlags.PreserveGraphicsClipping |
        TextFormatFlags.PreserveGraphicsTranslateTransform;

    public GlassLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(0, 2, 0, 2);
    }

    internal static Size Measure(string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFlags) + new Size(0, 4);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 0 ? proposedSize.Width : Width;
        return Measure(Text, Font, Math.Max(1, width - Padding.Horizontal)) + new Size(Padding.Horizontal, Padding.Vertical - 4);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        GlassTheme.PaintBackdrop(this, e.Graphics);
        var bounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
        var flags = TextFlags;
        if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.Top
        };
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor, flags);
    }
}
