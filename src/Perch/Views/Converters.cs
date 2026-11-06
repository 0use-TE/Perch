using System.Globalization;
using System.Windows.Data;

namespace Perch.Views;

// Two-way "this radio button is the selected string" binding for segmented choices.
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

// Boolean choice: the parameter "true" or "false" is the value the button stands for.
public sealed class BoolChoiceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && b.ToString().Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? bool.Parse(parameter!.ToString()!) : Binding.DoNothing;
}

// Tooltips sit centred under the control they describe: offset = (control width - tooltip width) / 2.
public sealed class CenterOffsetConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is double target && values[1] is double tip ? (target - tip) / 2 : 0d;
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => $"{System.Convert.ToInt32(value, culture)}%";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
