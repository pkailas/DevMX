using System;
using System.Globalization;
using System.Windows.Data;

namespace DevMX.App;

/// <summary>
/// Converts a string value to a boolean by comparing against a specified value (ConverterParameter).
/// </summary>
public class StringEqualsBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && value is string str)
        {
            return string.Equals(str, paramStr, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && value is bool b && b)
            return paramStr;
        return Binding.DoNothing;
    }
}
