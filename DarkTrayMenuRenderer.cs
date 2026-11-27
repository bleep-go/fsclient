using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace FsClient;

/// <summary>
/// Тёмный рендерер контекстного меню в системном трее: скруглённая панель,
/// подсветка пунктов и разделитель в стиле основного окна.
/// </summary>
public sealed class DarkTrayMenuRenderer : ToolStripRenderer
{
    private static readonly Color Background = Color.FromArgb(28, 25, 43);
    private static readonly Color Border = Color.FromArgb(36, 255, 255, 255);
    private static readonly Color Hover = Color.FromArgb(38, 124, 92, 255);
    private static readonly Color Text = Color.FromArgb(244, 242, 250);
    private static readonly Color Divider = Color.FromArgb(30, 255, 255, 255);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var rect = new Rectangle(e.AffectedBounds.Left, e.AffectedBounds.Top,
            e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);

        using var path = Rounded(rect, 12);
        using var pen = new Pen(Border);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
            return;

        using var path = Rounded(new Rectangle(Point.Empty, e.Item.Size), 8);
        using var brush = new SolidBrush(Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected || e.Item.Pressed ? Color.White : Text;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Size.Height / 2;
        using var pen = new Pen(Divider);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Size.Width - 10, y);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Иконок в пунктах нет — запас под них не нужен.
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(rect, new Size(radius, radius));
        return path;
    }
}
