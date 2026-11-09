using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace Perch.Views;

// A hint drawn over an empty text box ("Message...") has to go as soon as the input method starts composing: the typed
// letters are not in Text yet, but they are on screen, and the hint would be printed over them. Every text box tells its
// placeholder siblings (TextBlocks with the "Placeholder" class in the same panel) to hide while there is composition text.
public static class ImeGuard
{
    public static void Install()
    {
        // The composition text lives on the text box's presenter (its inner text surface).
        TextPresenter.PreeditTextProperty.Changed.AddClassHandler<TextPresenter>((presenter, _) =>
        {
            var box = presenter.FindAncestorOfType<TextBox>();
            if (box?.Parent is not Panel panel) return;
            var composing = !string.IsNullOrEmpty(presenter.PreeditText);
            foreach (var child in panel.Children)
                if (child is TextBlock hint && hint.Classes.Contains("Placeholder")) hint.Opacity = composing ? 0 : 1;
        });
    }
}
