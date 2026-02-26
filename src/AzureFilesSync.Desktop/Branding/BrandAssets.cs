using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzureFilesSync.Desktop.Branding;

public static class BrandAssets
{
    public const string AppIconRelativePath = "Assets/Branding/app.ico";
    public const string AppIconPackUri = "pack://application:,,,/Assets/Branding/app.ico";
    public const string Logo16RelativeUri = "/Assets/Branding/logo-16.png";
    public const string Logo24RelativeUri = "/Assets/Branding/logo-24.png";
    public const string Logo32RelativeUri = "/Assets/Branding/logo-32.png";
    public const string Logo48RelativeUri = "/Assets/Branding/logo-48.png";
    public const string Wordmark24RelativeUri = "/Assets/Branding/wordmark-24.png";
    public const string Wordmark32RelativeUri = "/Assets/Branding/wordmark-32.png";

    public static ImageSource CreateAppIcon() =>
        BitmapFrame.Create(new Uri(AppIconPackUri, UriKind.Absolute));

    public static ImageSource CreateImage(string relativeUri)
    {
        var normalized = relativeUri.StartsWith("/", StringComparison.Ordinal)
            ? relativeUri
            : "/" + relativeUri;
        return new BitmapImage(new Uri("pack://application:,,," + normalized, UriKind.Absolute));
    }
}
