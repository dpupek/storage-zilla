using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Branding;

namespace AzureFilesSync.Desktop.Dialogs;

public sealed class HelpWindow : Window
{
    private readonly IUserHelpContentService _helpContentService;
    private readonly TextBox _searchBox;
    private readonly ListBox _topicsList;
    private readonly WebBrowser _browser;
    private readonly TextBlock _statusText;
    private readonly List<HelpTopic> _allTopics;
    private HelpDocument? _currentDocument;
    private bool _initialized;

    private HelpWindow(IUserHelpContentService helpContentService)
    {
        _helpContentService = helpContentService;
        _allTopics = _helpContentService.GetTopics().ToList();

        Title = "Storage Zilla Help";
        Width = 1040;
        Height = 740;
        MinWidth = 860;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = BrandAssets.CreateAppIcon();

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SetColumn(_searchBox, 0);
        topBar.Children.Add(_searchBox);

        var openDocsButton = new Button
        {
            Content = "Open Doc File",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        openDocsButton.Click += (_, _) => OpenCurrentDocument();
        Grid.SetColumn(openDocsButton, 1);
        topBar.Children.Add(openDocsButton);

        var closeButton = new Button
        {
            Content = "Close",
            IsDefault = true,
            Padding = new Thickness(12, 4, 12, 4)
        };
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 2);
        topBar.Children.Add(closeButton);

        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _topicsList = new ListBox
        {
            DisplayMemberPath = nameof(HelpTopic.Title)
        };
        _topicsList.SelectionChanged += OnTopicSelectionChanged;
        Grid.SetColumn(_topicsList, 0);
        contentGrid.Children.Add(_topicsList);

        _browser = new WebBrowser();
        _browser.Navigating += OnBrowserNavigating;
        Grid.SetColumn(_browser, 2);
        contentGrid.Children.Add(_browser);

        _statusText = new TextBlock
        {
            Margin = new Thickness(8),
            Foreground = System.Windows.Media.Brushes.DimGray
        };
        Grid.SetColumn(_statusText, 2);
        contentGrid.Children.Add(_statusText);

        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        Content = root;

        ApplyFilter();
        ContentRendered += async (_, _) =>
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await LoadDefaultTopicAsync();
        };
    }

    public static void Show(Window? owner, IUserHelpContentService helpContentService)
    {
        var dialog = new HelpWindow(helpContentService)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void ApplyFilter()
    {
        var query = _searchBox.Text?.Trim() ?? string.Empty;
        var filtered = _allTopics
            .Where(x => query.Length == 0 || x.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _topicsList.ItemsSource = filtered;
    }

    private async void OnTopicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadSelectedTopicAsync();
    }

    private async Task LoadSelectedTopicAsync()
    {
        if (_topicsList.SelectedItem is not HelpTopic topic)
        {
            return;
        }

        await LoadTopicAsync(topic);
    }

    private async Task LoadTopicAsync(HelpTopic topic)
    {
        SelectTopic(topic);

        try
        {
            _statusText.Text = $"Loading: {topic.Title}";
            _currentDocument = await _helpContentService.LoadTopicAsync(topic.Id, CancellationToken.None);
            _browser.NavigateToString(_currentDocument.Html);
            _statusText.Text = _currentDocument.SourcePath;
        }
        catch (Exception ex)
        {
            _browser.NavigateToString("<html><body><h2>Help unavailable</h2><p>Could not load selected document.</p></body></html>");
            _statusText.Text = ex.Message;
        }
    }

    private async Task LoadDefaultTopicAsync()
    {
        var defaultTopic = _allTopics.FirstOrDefault(x => string.Equals(x.Id, "overview", StringComparison.OrdinalIgnoreCase))
            ?? _allTopics.FirstOrDefault();
        if (defaultTopic is null)
        {
            return;
        }

        await LoadTopicAsync(defaultTopic);
    }

    private void SelectTopic(HelpTopic topic)
    {
        if (_topicsList.ItemsSource is not IEnumerable<HelpTopic> topics)
        {
            return;
        }

        var item = topics.FirstOrDefault(x => string.Equals(x.Id, topic.Id, StringComparison.OrdinalIgnoreCase));
        if (item is not null && !ReferenceEquals(_topicsList.SelectedItem, item))
        {
            _topicsList.SelectedItem = item;
        }
    }

    private async void OnBrowserNavigating(object? sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri is null || _currentDocument is null)
        {
            return;
        }

        if (string.Equals(e.Uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(e.Uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            Process.Start(new ProcessStartInfo { FileName = e.Uri.ToString(), UseShellExecute = true });
            return;
        }

        if (string.Equals(e.Uri.Scheme, "help", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            var topicId = e.Uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(topicId))
            {
                var topic = _allTopics.FirstOrDefault(x => string.Equals(x.Id, topicId, StringComparison.OrdinalIgnoreCase));
                if (topic is not null)
                {
                    await LoadTopicAsync(topic);
                }
            }
            return;
        }

        var targetPath = ResolveTargetPath(e.Uri);
        if (targetPath is null)
        {
            return;
        }

        if (string.Equals(Path.GetExtension(targetPath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            var topic = FindTopicByPath(targetPath);
            if (topic is not null)
            {
                await LoadTopicAsync(topic);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
            return;
        }
    }

    private string? ResolveTargetPath(Uri uri)
    {
        if (uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (!uri.IsAbsoluteUri)
        {
            var sourceDir = Path.GetDirectoryName(_currentDocument!.SourcePath);
            if (string.IsNullOrWhiteSpace(sourceDir))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(sourceDir, uri.OriginalString));
        }

        return null;
    }

    private HelpTopic? FindTopicByPath(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return _allTopics.FirstOrDefault(x =>
            string.Equals(Path.GetFileName(x.RelativePath), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenCurrentDocument()
    {
        if (_currentDocument is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _currentDocument.SourcePath,
            UseShellExecute = true
        });
    }
}
