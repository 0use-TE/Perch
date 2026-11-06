using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class TrayPage : UserControl
{
    // True while a file is being dragged out of the shelf, so the island doesn't take it back in.
    public static bool DraggingOut { get; private set; }
    private Point pressed;
    private TrayFile? pressedFile;
    private PointerPressedEventArgs? pressedEvent;

    public TrayPage()
    {
        InitializeComponent();
        FileList.AddHandler(PointerPressedEvent, OnPress, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerMovedEvent, OnMove, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        FileList.DoubleTapped += OnDoubleClick;
    }

    private static TrayFile? FileAt(object? source)
    {
        for (var hit = source as Visual; hit != null; hit = hit.GetVisualParent())
            if (hit is Control { DataContext: TrayFile file }) return file;
        return null;
    }

    private void OnPress(object? sender, PointerPressedEventArgs e)
    {
        pressed = e.GetPosition(FileList);
        pressedEvent = e;
        pressedFile = e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed ? FileAt(e.Source) : null;
    }

    private async void OnMove(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed || pressedFile is not { } file || pressedEvent == null) return;
        var delta = e.GetPosition(FileList) - pressed;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        pressedFile = null;
        if (!file.Exists || DataContext is not TrayViewModel tray || TopLevel.GetTopLevel(this) is not { } top) return;
        DraggingOut = true;
        try
        {
            IStorageItem? item = file.IsFolder ? await top.StorageProvider.TryGetFolderFromPathAsync(file.FullPath) : await top.StorageProvider.TryGetFileFromPathAsync(file.FullPath);
            if (item == null) return;
#pragma warning disable CS0618
            var data = new DataObject();
            data.Set(DataFormats.Files, new[] { item });
            var result = await DragDrop.DoDragDrop(pressedEvent, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
#pragma warning restore CS0618
            if (result.HasFlag(DragDropEffects.Move) && !file.Exists) tray.RemoveFile(file);
        }
        finally { DraggingOut = false; }
    }

    private void OnDoubleClick(object? sender, TappedEventArgs e)
    {
        if (FileAt(e.Source) is { } file && DataContext is TrayViewModel tray) tray.Open.Execute(file);
    }
}
