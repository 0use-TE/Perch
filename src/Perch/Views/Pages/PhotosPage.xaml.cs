using System.Windows.Controls;
using System.Windows.Input;

namespace Perch.Views.Pages;

public partial class PhotosPage : UserControl
{
    public PhotosPage() => InitializeComponent();

    // The strip scrolls sideways with the ordinary mouse wheel.
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
