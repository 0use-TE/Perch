using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class AiPage : UserControl
{
    private AiViewModel? model;
    private bool followTail = true;
    private bool programmaticScroll;

    public FlyoutBase? ModelFlyout => ModelButton.Flyout;

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
        // Rolling the wheel up pauses auto-scrolling so the user can read earlier lines while an answer streams.
        Scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0) followTail = false;
            else if (e.Delta.Y < 0 && NearBottom()) followTail = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Scroller.ScrollChanged += (_, e) =>
        {
            if (programmaticScroll) return;
            if (e.OffsetDelta.Y < -0.5) followTail = false;
            else if (e.OffsetDelta.Y > 0.5 && NearBottom()) followTail = true;
        };
    }

    private bool NearBottom() => Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y <= 20;
    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e) { if (model?.CanRename == true) model.BeginRename.Execute(null); }
    private void OnTitleLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => model?.CommitRename();
    private void CloseModelMenu() => ModelButton.Flyout?.Hide();
    private void FocusTitle() => Dispatcher.UIThread.Post(() => { TitleBox.Focus(); TitleBox.SelectAll(); }, DispatcherPriority.Input);

    // New turns scroll to the bottom; streaming growth only follows when the user hasn't scrolled up.
    private void ScrollToEnd(bool force)
    {
        if (force) followTail = true;
        if (!followTail) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!followTail) return;
            programmaticScroll = true;
            try { Scroller.ScrollToEnd(); }
            finally { programmaticScroll = false; }
        }, DispatcherPriority.Background);
    }
}
