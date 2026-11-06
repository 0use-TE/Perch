using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Perch.Views.Pages;

public partial class PhotosPage : UserControl
{
    public PhotosPage()
    {
        InitializeComponent();
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // The strip scrolls sideways with the ordinary mouse wheel.
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        Scroller.Offset = new Vector(Math.Max(0, Scroller.Offset.X - e.Delta.Y * 60), 0);
        e.Handled = true;
    }
}
