using System.Windows;
using System.Windows.Controls;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Desktop.Dialogs;

public sealed class TransferSettingsWindow : Window
{
    private readonly TextBox _concurrencyBox;
    private readonly TextBox _throttleKbBox;
    private readonly ComboBox _uploadConflictPolicyBox;
    private readonly ComboBox _downloadConflictPolicyBox;

    private TransferSettingsWindow(
        int currentConcurrency,
        int currentThrottleKb,
        TransferConflictPolicy currentUploadConflictPolicy,
        TransferConflictPolicy currentDownloadConflictPolicy)
    {
        Title = "Transfer Settings";
        Width = 440;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var concurrencyLabel = new TextBlock
        {
            Text = "Max concurrency (1-32):",
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(concurrencyLabel, 0);
        root.Children.Add(concurrencyLabel);

        _concurrencyBox = new TextBox
        {
            Text = currentConcurrency.ToString(),
            MinWidth = 380
        };
        Grid.SetRow(_concurrencyBox, 1);
        root.Children.Add(_concurrencyBox);

        var throttleLabel = new TextBlock
        {
            Text = "Throttle KB/s (0 = unlimited):",
            Margin = new Thickness(0, 12, 0, 6)
        };
        Grid.SetRow(throttleLabel, 2);
        root.Children.Add(throttleLabel);

        _throttleKbBox = new TextBox
        {
            Text = currentThrottleKb.ToString(),
            MinWidth = 380
        };
        Grid.SetRow(_throttleKbBox, 3);
        root.Children.Add(_throttleKbBox);

        var uploadConflictLabel = new TextBlock
        {
            Text = "Default upload conflict policy:",
            Margin = new Thickness(0, 12, 0, 6)
        };
        Grid.SetRow(uploadConflictLabel, 4);
        root.Children.Add(uploadConflictLabel);

        _uploadConflictPolicyBox = new ComboBox
        {
            MinWidth = 380,
            ItemsSource = Enum.GetValues<TransferConflictPolicy>(),
            SelectedItem = currentUploadConflictPolicy
        };
        Grid.SetRow(_uploadConflictPolicyBox, 5);
        root.Children.Add(_uploadConflictPolicyBox);

        var downloadConflictLabel = new TextBlock
        {
            Text = "Default download conflict policy:",
            Margin = new Thickness(0, 12, 0, 6)
        };
        Grid.SetRow(downloadConflictLabel, 6);
        root.Children.Add(downloadConflictLabel);

        _downloadConflictPolicyBox = new ComboBox
        {
            MinWidth = 380,
            ItemsSource = Enum.GetValues<TransferConflictPolicy>(),
            SelectedItem = currentDownloadConflictPolicy
        };
        Grid.SetRow(_downloadConflictPolicyBox, 7);
        root.Children.Add(_downloadConflictPolicyBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "Save", Width = 90, IsDefault = true };
        ok.Click += OnSaveClicked;
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 8);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _concurrencyBox.Focus();
            _concurrencyBox.SelectAll();
        };
    }

    public int MaxConcurrency { get; private set; }
    public int MaxThrottleKilobytesPerSecond { get; private set; }
    public TransferConflictPolicy UploadConflictPolicy { get; private set; }
    public TransferConflictPolicy DownloadConflictPolicy { get; private set; }

    public static bool TryShow(
        Window owner,
        int currentConcurrency,
        int currentThrottleKb,
        TransferConflictPolicy currentUploadConflictPolicy,
        TransferConflictPolicy currentDownloadConflictPolicy,
        out int newConcurrency,
        out int newThrottleKb,
        out TransferConflictPolicy uploadConflictPolicy,
        out TransferConflictPolicy downloadConflictPolicy)
    {
        var dialog = new TransferSettingsWindow(
            currentConcurrency,
            currentThrottleKb,
            currentUploadConflictPolicy,
            currentDownloadConflictPolicy)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() == true)
        {
            newConcurrency = dialog.MaxConcurrency;
            newThrottleKb = dialog.MaxThrottleKilobytesPerSecond;
            uploadConflictPolicy = dialog.UploadConflictPolicy;
            downloadConflictPolicy = dialog.DownloadConflictPolicy;
            return true;
        }

        newConcurrency = currentConcurrency;
        newThrottleKb = currentThrottleKb;
        uploadConflictPolicy = currentUploadConflictPolicy;
        downloadConflictPolicy = currentDownloadConflictPolicy;
        return false;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_concurrencyBox.Text.Trim(), out var concurrency))
        {
            MessageBox.Show(this, "Concurrency must be a whole number.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(_throttleKbBox.Text.Trim(), out var throttleKb))
        {
            MessageBox.Show(this, "Throttle must be a whole number.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MaxConcurrency = Math.Clamp(concurrency, 1, 32);
        MaxThrottleKilobytesPerSecond = Math.Max(0, throttleKb);
        UploadConflictPolicy = _uploadConflictPolicyBox.SelectedItem is TransferConflictPolicy upload
            ? upload
            : TransferConflictPolicy.Ask;
        DownloadConflictPolicy = _downloadConflictPolicyBox.SelectedItem is TransferConflictPolicy download
            ? download
            : TransferConflictPolicy.Ask;
        DialogResult = true;
    }
}
