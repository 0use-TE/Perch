using Avalonia.Controls;

namespace Perch.Views.Pages;

public partial class NotesPage : UserControl
{
    public NotesPage() => InitializeComponent();

    // Puts the caret in the title of the selected note (after the "new note" shortcut).
    public void FocusTitle() => TitleBox.Focus();
}
