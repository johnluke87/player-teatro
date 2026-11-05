using System.Globalization;
using System.Windows.Data;

namespace CuePlayer;

/// <summary>0 → "subito", 3 → "3 s".</summary>
public sealed class SecondsLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && d <= 0 ? "subito" : $"{value} s";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the two bound values are equal (chip = current choice).</summary>
public sealed class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when every bound value is true (e.g. "has a note" AND "notes feature on").</summary>
public sealed class AllTrueVisibilityConverter : IMultiValueConverter
{
    /// <summary>ConverterParameter="invert": visible when NOT all are true.</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool all = values.All(v => v is true);
        if (parameter as string == "invert") all = !all;
        return all ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
