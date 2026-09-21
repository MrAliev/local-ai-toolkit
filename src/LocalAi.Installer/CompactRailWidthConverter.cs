using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalAi.Installer;

public sealed class CompactRailWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(value is double width && width < 760 ? 132 : double.Parse((string)parameter, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
