using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kaku.App.Views;

public static class Converters
{
    /// <summary>double → Thickness(left, 0, 0, 0).</summary>
    public static readonly IValueConverter LeftMargin = new LeftMarginConverter();

    private sealed class LeftMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new Thickness(value is double d ? d : 0, 0, 0, 0);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
