using System.Drawing.Drawing2D;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingRectangleF = System.Drawing.RectangleF;
using DrawingSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace ZZZSwitch;

internal static class TrayContextMenu
{
    internal const int MenuWidth = 184;
    internal const int ItemHeight = 30;

    public static Forms.ContextMenuStrip Create(
        bool isDark,
        string showFullText,
        string showCompactText,
        string exitText,
        Action showFull,
        Action showCompact,
        Action exit)
    {
        var palette = isDark
            ? new Palette(
                Background: DrawingColor.FromArgb(34, 34, 34),
                Hover: DrawingColor.FromArgb(46, 46, 46),
                Border: DrawingColor.FromArgb(72, 72, 72),
                Text: DrawingColor.FromArgb(242, 242, 242))
            : new Palette(
                Background: DrawingColor.FromArgb(255, 255, 255),
                Hover: DrawingColor.FromArgb(232, 232, 232),
                Border: DrawingColor.FromArgb(168, 168, 168),
                Text: DrawingColor.FromArgb(28, 28, 28));

        var menu = new Forms.ContextMenuStrip
        {
            AutoSize = true,
            BackColor = palette.Background,
            ForeColor = palette.Text,
            Font = new DrawingFont("Segoe UI", 9.5f),
            MinimumSize = new DrawingSize(MenuWidth, 0),
            Padding = new Forms.Padding(5),
            Renderer = new Renderer(palette),
            ShowCheckMargin = false,
            ShowImageMargin = false
        };

        menu.Items.Add(CreateItem(showFullText, showFull));
        menu.Items.Add(CreateItem(showCompactText, showCompact));
        menu.Items.Add(new Forms.ToolStripSeparator
        {
            AutoSize = false,
            Size = new DrawingSize(MenuWidth - 10, 9)
        });
        menu.Items.Add(CreateItem(exitText, exit));
        menu.Opening += (_, _) => ApplyRoundedRegion(menu);
        menu.SizeChanged += (_, _) => ApplyRoundedRegion(menu);
        return menu;
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            AutoSize = false,
            ForeColor = DrawingColor.Empty,
            Margin = Forms.Padding.Empty,
            Padding = Forms.Padding.Empty,
            Size = new DrawingSize(MenuWidth - 10, ItemHeight)
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static void ApplyRoundedRegion(Forms.ContextMenuStrip menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
        {
            return;
        }

        using var path = RoundedPath(
            new DrawingRectangleF(0, 0, menu.Width, menu.Height),
            8);
        var oldRegion = menu.Region;
        menu.Region = new System.Drawing.Region(path);
        oldRegion?.Dispose();
    }

    private static GraphicsPath RoundedPath(DrawingRectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed record Palette(
        DrawingColor Background,
        DrawingColor Hover,
        DrawingColor Border,
        DrawingColor Text);

    private sealed class Renderer(Palette palette) : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(palette.Background);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(
                new DrawingRectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                8);
            using var pen = new System.Drawing.Pen(palette.Border);
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                return;
            }

            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(
                new DrawingRectangleF(3, 2, e.Item.Width - 6, e.Item.Height - 4),
                5);
            using var brush = new System.Drawing.SolidBrush(palette.Hover);
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var bounds = new DrawingRectangle(
                12,
                0,
                Math.Max(0, e.Item.Width - 24),
                e.Item.Height);
            Forms.TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                bounds,
                palette.Text,
                Forms.TextFormatFlags.Left |
                Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(palette.Border);
            e.Graphics.DrawLine(pen, new DrawingPoint(10, y), new DrawingPoint(e.Item.Width - 10, y));
        }
    }
}
