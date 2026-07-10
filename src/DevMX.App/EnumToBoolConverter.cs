using System;
using System.Globalization;
using System.Windows.Data;

namespace DevMX.App;

/// <summary>
/// Converts an enum value to a boolean by comparing against a specified value (ConverterParameter).
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && value != null)
        {
            try
            {
                var enumType = value.GetType();
                var target = Enum.Parse(enumType, paramStr);
                return Equals(value, target);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && targetType.IsEnum)
        {
            if (value is bool b && b)
                return Enum.Parse(targetType, paramStr);
        }
        return Binding.DoNothing;
    }
}
