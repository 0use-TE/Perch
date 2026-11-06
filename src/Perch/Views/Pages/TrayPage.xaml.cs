using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class TrayPage : UserControl
{
    // True while a file is being dragged out of the shelf, so the island doesn't take it back in.
    public static bool DraggingOut { get; private set; }
    private Point pressed;
    private TrayFile? pressedFile;

    public TrayPage() => InitializeComponent();

    private static TrayFile? FileAt(object source)
    {
        var hit = source as DependencyObject;
        while (hit != null)
        {
            if (hit is FrameworkElement { DataContext: TrayFile file }) return file;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return null;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        pressed = e.GetPosition(FileList);
        pressedFile = FileAt(e.OriginalSource);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressedFile is not { } file) return;
        var delta = e.GetPosition(FileList) - pressed;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        pressedFile = null;
        if (!file.Exists || DataContext is not TrayViewModel tray) return;
        DraggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { file.FullPath });
            var result = DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result.HasFlag(DragDropEffects.Move) && !file.Exists) tray.RemoveFile(file);
        }
        finally { DraggingOut = false; }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileAt(e.OriginalSource) is { } file && DataContext is TrayViewModel tray) tray.Open.Execute(file);
    }
}
