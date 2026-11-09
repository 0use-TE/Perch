using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class AiPage : UserControl
{
    private AiViewModel? model;

    public AiPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (model != null) { model.Scroll -= ScrollToEnd; model.RenameStarted -= FocusTitle; model.ModelPicked -= CloseModelMenu; }
            model = DataContext as AiViewModel;
            if (model != null) { model.Scroll += ScrollToEnd; model.RenameStarted += FocusTitle; model.ModelPicked += CloseModelMenu; }
        };
        TitleBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; model?.CommitRename(); }
            else if (e.Key == Key.Escape) { e.Handled = true; model?.CancelRename(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Enter sends, Shift+Enter starts a new line (the box allows several lines).
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            if (model?.Send.CanExecute(null) == true) model.Send.Execute(null);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e) { if (model?.CanRename == true) model.BeginRename.Execute(null); }
    private void OnTitleLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => model?.CommitRename();
    private void CloseModelMenu() => ModelButton.Flyout?.Hide();
    private void FocusTitle() => Dispatcher.UIThread.Post(() => { TitleBox.Focus(); TitleBox.SelectAll(); }, DispatcherPriority.Input);

    // New or growing messages keep the conversation scrolled to the bottom.
    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
}
