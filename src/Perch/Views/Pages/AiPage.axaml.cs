using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Perch.Services;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class AiPage : UserControl
{
    private AiViewModel? model;
    private bool followTail = true;
    private bool programmaticScroll;

    public FlyoutBase? ModelFlyout => ModelButton.Flyout;
    public FlyoutBase? WorkspaceFlyout => WorkspaceButton.Flyout;
    public event Action<bool>? PickingFolderChanged;

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
        // Enter sends, Shift+Enter starts a new line (the box allows several lines); Ctrl+V pastes an image if present.
        Input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (Ui.Clipboard is { } clipboard && await clipboard.TryGetBitmapAsync() is { } bmp)
                {
                    e.Handled = true;
                    using (bmp)
                    {
                        using var ms = new MemoryStream();
                        bmp.Save(ms);
                        model?.AttachPng(ms.ToArray(), bmp.PixelSize.Width, bmp.PixelSize.Height);
                    }
                }
                return;
            }
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

    private async void OnAddWorkspace(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        PickingFolderChanged?.Invoke(true);
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Ai.WorkspacePick"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) model?.AddWorkspace(path);
        }
        finally
        {
            PickingFolderChanged?.Invoke(false);
        }
    }

    private void OnPickNoRepo(object? sender, RoutedEventArgs e)
    {
        if (model != null) model.WorkspacePath = "";
        WorkspaceButton.Flyout?.Hide();
    }

    private void OnPickedWorkspace(object? sender, RoutedEventArgs e) => WorkspaceButton.Flyout?.Hide();

    private async void OnPickWorkspace(object? sender, RoutedEventArgs e)
    {
        WorkspaceButton.Flyout?.Hide();
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        PickingFolderChanged?.Invoke(true);
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Ai.WorkspacePick"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path && model != null)
                model.WorkspacePath = path;
        }
        finally
        {
            PickingFolderChanged?.Invoke(false);
        }
    }
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
