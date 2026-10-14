using System.Globalization;
using System.Windows.Data;

namespace ClawBox.Desktop;

public sealed class HeaderWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Math.Max(0, System.Convert.ToDouble(value, culture) - 30);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
