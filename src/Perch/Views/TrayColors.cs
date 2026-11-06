using System.Drawing;
using System.Windows.Forms;

namespace Perch.Views;

// Gives the notification-area menu (a WinForms ContextMenuStrip) the same dark or light look as the WPF menu.
internal sealed class TrayColors : ProfessionalColorTable
{
    private readonly bool light;
    public TrayColors(bool light) { this.light = light; UseSystemColors = false; }

    private Color Back => light ? Color.FromArgb(250, 250, 251) : Color.FromArgb(10, 10, 11);
    private Color Hover => light ? Color.FromArgb(228, 228, 232) : Color.FromArgb(52, 52, 56);
    private Color Line => light ? Color.FromArgb(214, 214, 218) : Color.FromArgb(56, 56, 60);

    public override Color ToolStripDropDownBackground => Back;
    public override Color ImageMarginGradientBegin => Back;
    public override Color ImageMarginGradientMiddle => Back;
    public override Color ImageMarginGradientEnd => Back;
    public override Color MenuBorder => Line;
    public override Color MenuItemBorder => Hover;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color SeparatorDark => Line;
    public override Color SeparatorLight => Line;
    public override Color CheckBackground => Hover;
    public override Color CheckSelectedBackground => Hover;
    public override Color CheckPressedBackground => Hover;

    public static void Apply(ContextMenuStrip menu, bool light)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new TrayColors(light)) { RoundedEdges = false };
        menu.BackColor = light ? Color.FromArgb(250, 250, 251) : Color.FromArgb(10, 10, 11);
        menu.ForeColor = light ? Color.FromArgb(22, 22, 26) : Color.FromArgb(242, 242, 244);
        foreach (ToolStripItem item in menu.Items) Recolor(item, menu.ForeColor);
    }

    private static void Recolor(ToolStripItem item, Color fore)
    {
        item.ForeColor = fore;
        if (item is ToolStripMenuItem sub) foreach (ToolStripItem child in sub.DropDownItems) Recolor(child, fore);
    }
}
