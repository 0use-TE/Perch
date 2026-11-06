using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Platform.Windows;

namespace Perch.Views;

// Frosted glass for the right-click menu and its submenus: like the island, each popup is hidden from screen capture,
// the screen behind it is sampled, blurred and painted under a translucent tint.
internal static class MenuFrost
{
    public static void Attach(ContextMenu menu, bool light)
    {
        void Top() => Frost(menu.Template.FindName("PART_Frost", menu) as Border, menu.Template.FindName("PART_Tint", menu) as Border, light);
        menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Top);
        menu.AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not MenuItem item || item.Template == null) return;
            item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                Frost(item.Template.FindName("PART_SubFrost", item) as Border, item.Template.FindName("PART_SubTint", item) as Border, light));
        }));
    }

    private static void Frost(Border? frost, Border? tint, bool light)
    {
        if (frost == null || tint == null || PresentationSource.FromVisual(frost) is not HwndSource source) return;
        var handle = source.Handle;
        if (!SetWindowDisplayAffinity(handle, 0x11)) return;
        // Give the compositor a moment to drop the popup from the capture before sampling.
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (!GetWindowRect(handle, out var r)) return;
            var picture = BackdropCapture.Grab(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), 6);
            if (picture == null) return;
            // The picture covers the whole popup window; the frost border sits inside its shadow margin.
            frost.Background = new ImageBrush(picture)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(-frost.Margin.Left, -frost.Margin.Top, frost.ActualWidth + frost.Margin.Left + frost.Margin.Right, frost.ActualHeight + frost.Margin.Top + frost.Margin.Bottom),
            };
            tint.Background = new SolidColorBrush(light ? Color.FromArgb(0xB4, 250, 250, 252) : Color.FromArgb(0xA6, 8, 10, 16));
        };
        wait.Start();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect32 rect);
}
