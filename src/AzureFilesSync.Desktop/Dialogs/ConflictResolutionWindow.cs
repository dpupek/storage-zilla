using System.Windows;
using System.Windows.Controls;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Desktop.Dialogs;

public enum ConflictPromptAction
{
    Overwrite,
    Rename,
    Skip,
    CancelBatch
}

public sealed class ConflictResolutionWindow : Window
{
    private readonly CheckBox _doForAll;

    private ConflictResolutionWindow(
        TransferDirection direction,
        string sourcePath,
        string destinationPath)
    {
        Title = "File Conflict";
        Width = 620;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = direction == TransferDirection.Upload
                ? "A file with the same name already exists in Azure Files."
                : "A file with the same name already exists locally.",
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var source = new TextBlock
        {
            Text = $"Source: {sourcePath}",
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(source, 1);
        root.Children.Add(source);

        var destination = new TextBlock
        {
            Text = $"Destination: {destinationPath}",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(destination, 2);
        root.Children.Add(destination);

        _doForAll = new CheckBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            Content = "Do for all conflicts in this selected batch"
        };
        Grid.SetRow(_doForAll, 3);
        root.Children.Add(_doForAll);

        var hint = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = System.Windows.Media.Brushes.DimGray,
            Text = "Choose Overwrite, Rename, Skip this conflict, or Cancel Batch."
        };
        Grid.SetRow(hint, 4);
        root.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(BuildActionButton("Overwrite", ConflictPromptAction.Overwrite, isDefault: true));
        buttons.Children.Add(BuildActionButton("Rename", ConflictPromptAction.Rename));
        buttons.Children.Add(BuildActionButton("Skip", ConflictPromptAction.Skip));
        buttons.Children.Add(BuildActionButton("Cancel Batch", ConflictPromptAction.CancelBatch, isCancel: true));
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        Content = root;
    }

    public ConflictPromptAction SelectedAction { get; private set; } = ConflictPromptAction.Skip;
    public bool DoForAll => _doForAll.IsChecked == true;

    public static bool TryShow(
        Window owner,
        TransferDirection direction,
        string sourcePath,
        string destinationPath,
        out ConflictPromptAction action,
        out bool doForAll)
    {
        var dialog = new ConflictResolutionWindow(direction, sourcePath, destinationPath)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() == true)
        {
            action = dialog.SelectedAction;
            doForAll = dialog.DoForAll;
            return true;
        }

        action = ConflictPromptAction.CancelBatch;
        doForAll = false;
        return false;
    }

    private Button BuildActionButton(string label, ConflictPromptAction action, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = label,
            Width = 110,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            DialogResult = true;
        };

        return button;
    }
}
