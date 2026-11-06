using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Perch.Views;

// Two-way "this radio button is the selected string" binding for segmented choices.
public sealed class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? parameter : BindingOperations.DoNothing;
}

// Boolean choice: the parameter "true" or "false" is the value the button stands for.
public sealed class BoolChoiceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && b.ToString().Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? bool.Parse(parameter!.ToString()!) : BindingOperations.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => $"{System.Convert.ToInt32(value, culture)}%";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// "a|b": a when the bound value is true, b otherwise; the result is made to fit the target property
// (brush, number, text decoration or text). This stands in for WPF's DataTriggers.
public sealed class PickConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "|").Split('|', 2);
        var pick = value is true ? parts[0] : parts.Length > 1 ? parts[1] : "";
        if (targetType == typeof(Avalonia.Media.IBrush) || targetType == typeof(Avalonia.Media.Brush)) return Avalonia.Media.Brush.Parse(pick);
        if (targetType == typeof(double)) return double.Parse(pick, CultureInfo.InvariantCulture);
        if (targetType == typeof(Avalonia.Media.TextDecorationCollection)) return pick is "" or "None" ? null : Avalonia.Media.TextDecorationCollection.Parse(pick);
        return pick;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
