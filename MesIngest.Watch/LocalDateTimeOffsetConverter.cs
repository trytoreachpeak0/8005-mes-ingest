using System.Globalization;
using System.Windows.Data;

namespace MesIngest.Watch;

internal sealed class LocalDateTimeOffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DateTimeOffset dto => WatchTimeDisplay.Format(dto),
            null => string.Empty,
            _ => value,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
