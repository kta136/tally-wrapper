using System.Runtime.InteropServices;

namespace ShowroomBilling.ServerTray.Ui;

/// <summary>
/// The tray icon's menu in the design's colours: white with a hairline, 32 px
/// rows, the row under the pointer on the hover grey, the item that stops the
/// server last and in red.
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private TrayMenuRenderer()
        : base(new Colours())
    {
        RoundedEdges = false;
    }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuRenderer();
        menu.Font = Theme.Font(TextRole.Body);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.BackColor = Theme.Panel;
        menu.ForeColor = Theme.Ink;
        menu.Padding = new Padding(4);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem)
            {
                item.Padding = new Padding(6, 5, 6, 5);
            }
        }

        menu.HandleCreated += (_, _) => RoundCorners(menu.Handle);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new RectangleF(PointF.Empty, e.Item.Size);
        using (var panel = new SolidBrush(Theme.Panel))
        {
            e.Graphics.FillRectangle(panel, bounds);
        }

        if (e.Item.Selected && e.Item.Enabled)
        {
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusChip * e.ToolStrip!.DeviceDpi / 96f, Theme.Hover);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Theme.InkDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Divider);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    /// <summary>Windows 11 rounds a popup's corners when asked; Windows 10 ignores the request.</summary>
    private static void RoundCorners(IntPtr handle)
    {
        const int cornerPreference = 33;
        var small = 3;
        try
        {
            _ = DwmSetWindowAttribute(handle, cornerPreference, ref small, sizeof(int));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Square corners, then.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private sealed class Colours : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;

        public override Color MenuBorder => Theme.Border;

        public override Color MenuItemBorder => Theme.Hover;

        public override Color MenuItemSelected => Theme.Hover;

        public override Color ImageMarginGradientBegin => Theme.Panel;

        public override Color ImageMarginGradientMiddle => Theme.Panel;

        public override Color ImageMarginGradientEnd => Theme.Panel;

        public override Color SeparatorDark => Theme.Divider;

        public override Color SeparatorLight => Theme.Panel;
    }
}
