using System.Windows;
using System.Windows.Controls;

namespace AzureFilesSync.Desktop.Dialogs;

public sealed class RenamePromptWindow : Window
{
    private readonly TextBox _textBox;

    private RenamePromptWindow(string title, string currentName)
    {
        Title = title;
        Width = 420;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new Grid { Margin = new Thickness(12) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "New name:",
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(label, 0);
        panel.Children.Add(label);

        _textBox = new TextBox
        {
            Text = currentName,
            MinWidth = 360
        };
        Grid.SetRow(_textBox, 1);
        panel.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }

    public static bool TryShow(Window owner, string title, string currentName, out string newName)
    {
        var dialog = new RenamePromptWindow(title, currentName)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() == true)
        {
            newName = dialog._textBox.Text.Trim();
            return !string.IsNullOrWhiteSpace(newName);
        }

        newName = string.Empty;
        return false;
    }
}
