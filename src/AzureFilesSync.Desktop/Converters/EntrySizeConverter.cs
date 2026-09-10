using AzureFilesSync.Core.Models;
using System.Globalization;
using System.Windows.Data;

namespace AzureFilesSync.Desktop.Converters;

public sealed class EntrySizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (isDirectory, isParent, length) = value switch
        {
            LocalEntry local => (local.IsDirectory, local.Name == "..", local.Length),
            RemoteEntry remote => (remote.IsDirectory, remote.Name == "..", remote.Length),
            long rawLength => (false, false, rawLength),
            _ => (true, true, 0L)
        };

        if (isDirectory || isParent || length < 0)
        {
            return string.Empty;
        }

        return FormatBytes(length, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string FormatBytes(long bytes, CultureInfo culture)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value.ToString("0.##", culture)} {units[unitIndex]}";
    }
}
