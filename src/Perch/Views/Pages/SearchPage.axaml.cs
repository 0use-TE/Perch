using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class SearchPage : UserControl
{
    public SearchPage()
    {
        InitializeComponent();
        // Typing stays in the box: the arrow keys move through the results and Enter uses the highlighted one.
        Box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not SearchViewModel model) return;
            switch (e.Key)
            {
                case Key.Down: model.MoveSelection(1); e.Handled = true; break;
                case Key.Up: model.MoveSelection(-1); e.Handled = true; break;
                case Key.Enter: model.ActivateSelected(); e.Handled = true; break;
                case Key.Escape: model.Query = ""; e.Handled = true; break;
            }
            if (e.Handled) Dispatcher.UIThread.Post(() => { if (model.Selected != null) Results.ScrollIntoView(model.Selected); }, DispatcherPriority.Background);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // The page was opened by its shortcut: the box is ready for typing at once.
    public void FocusBox() => Dispatcher.UIThread.Post(() => { Box.Focus(); Box.SelectAll(); }, DispatcherPriority.Input);
}
