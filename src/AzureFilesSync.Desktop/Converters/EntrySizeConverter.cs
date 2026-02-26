using AzureFilesSync.Core.Models;
using Humanizer.Bytes;
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

        return ByteSize.FromBytes(length).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
