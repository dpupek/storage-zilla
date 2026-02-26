using System.Globalization;
using System.Windows.Data;

namespace AzureFilesSync.Desktop.Converters;

public sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToLocalTime().ToString("g", culture),
            DateTime dt => dt.ToLocalTime().ToString("g", culture),
            null => string.Empty,
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
