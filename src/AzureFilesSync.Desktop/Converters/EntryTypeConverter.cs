using AzureFilesSync.Core.Models;
using System.Globalization;
using System.Windows.Data;

namespace AzureFilesSync.Desktop.Converters;

public sealed class EntryTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            LocalEntry { Name: ".." } => "Parent",
            RemoteEntry { Name: ".." } => "Parent",
            LocalEntry { IsDirectory: true } => "Folder",
            RemoteEntry { IsDirectory: true } => "Folder",
            LocalEntry => "File",
            RemoteEntry => "File",
            bool isDirectory => isDirectory ? "Folder" : "File",
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
