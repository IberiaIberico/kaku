using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kaku.App.ViewModels;

public static class SearchHitItemConverters
{
    /// <summary>true → strikethrough (the match that will be replaced).</summary>
    public static readonly IValueConverter Strike = new StrikeConverter();

    private sealed class StrikeConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? TextDecorations.Strikethrough : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
