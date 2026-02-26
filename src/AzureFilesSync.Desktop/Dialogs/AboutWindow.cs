using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AzureFilesSync.Desktop.Branding;

namespace AzureFilesSync.Desktop.Dialogs;

public sealed class AboutWindow : Window
{
    private AboutWindow(string version)
    {
        Title = "About Storage Zilla";
        Width = 460;
        Height = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = BrandAssets.CreateAppIcon();

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new Image
        {
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Source = BrandAssets.CreateImage(BrandAssets.Wordmark32RelativeUri)
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var title = new TextBlock
        {
            Text = "Storage Zilla",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F2937"))
        };
        Grid.SetRow(title, 1);
        root.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = $"Version {version}\nAzure Files desktop transfer client",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(subtitle, 2);
        root.Children.Add(subtitle);

        var detailsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        var body = new TextBlock
        {
            Text = "Storage Zilla helps developers browse, sync, and transfer Azure Files with an FTP-style dual-pane workflow.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray
        };
        detailsPanel.Children.Add(body);

        var licensing = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        licensing.Inlines.Add(new Run("Licensing: "));
        licensing.Inlines.Add(CreateLink("GPL-3.0-or-later", "https://github.com/dpupek/storage-zilla/blob/main/LICENSE"));
        licensing.Inlines.Add(new Run(" or "));
        licensing.Inlines.Add(CreateLink("Commercial License", "https://github.com/dpupek/storage-zilla/blob/main/LICENSE-COMMERCIAL"));
        detailsPanel.Children.Add(licensing);

        Grid.SetRow(detailsPanel, 3);
        root.Children.Add(detailsPanel);

        var close = new Button
        {
            Content = "Close",
            Width = 100,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => DialogResult = true;
        Grid.SetRow(close, 4);
        root.Children.Add(close);

        Content = root;
    }

    public static void Show(Window? owner, string version)
    {
        var dialog = new AboutWindow(version)
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private static Hyperlink CreateLink(string label, string url)
    {
        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = new Uri(url),
            ToolTip = url
        };
        link.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        };
        return link;
    }
}
