using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Perch.Views;

// Forces every {DynamicResource} in a window to be looked up again. WPF normally does this itself when the string or
// colour dictionaries are swapped, but the settings window does not reliably get that refresh (its references keep the
// old cached value), so language and theme changes would otherwise show up only after reopening it.
// Each reference is re-created from its key with SetResourceReference, which starts with an empty cache.
internal static class ResourceRefresh
{
    private static readonly PropertyInfo? KeyProperty = Type.GetType("System.Windows.ResourceReferenceExpression, PresentationFramework")
        ?.GetProperty("ResourceKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static void Run(DependencyObject root) => Visit(root, new HashSet<DependencyObject>());

    private static void Visit(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(d)) return;
        List<(DependencyProperty Property, object Key)>? stale = null;
        var values = d.GetLocalValueEnumerator();
        while (values.MoveNext())
            if (values.Current.Value is Expression expression && expression.GetType().Name == "ResourceReferenceExpression" && KeyProperty?.GetValue(expression) is { } key)
                (stale ??= []).Add((values.Current.Property, key));
        if (stale != null)
            foreach (var (property, key) in stale)
            {
                if (d is FrameworkElement element) element.SetResourceReference(property, key);
                else if (d is FrameworkContentElement content) content.SetResourceReference(property, key);
            }

        foreach (var child in LogicalTreeHelper.GetChildren(d))
            if (child is DependencyObject logical) Visit(logical, seen);
        if (d is Visual or Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Visit(VisualTreeHelper.GetChild(d, i), seen);
    }
}
