using System.Windows;

namespace AzureFilesSync.Desktop.Dialogs;

public partial class ErrorDialogWindow : Window
{
    public string Summary { get; }
    public string ErrorDetails { get; }

    public ErrorDialogWindow(string summary, string errorDetails)
    {
        Summary = summary;
        ErrorDetails = errorDetails;

        InitializeComponent();
        DataContext = this;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ErrorDetails);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
