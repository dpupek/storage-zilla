using Serilog;
using System.Text;
using System.Windows;

namespace AzureFilesSync.Desktop.Dialogs;

public static class ErrorDialog
{
    public static void Show(string summary, Exception exception)
    {
        var details = BuildDetails(summary, exception);
        Log.Error(exception, "{Summary}", summary);

        var owner = Application.Current?.MainWindow;
        var dialog = new ErrorDialogWindow(summary, details);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    public static void ShowMessage(string summary, string details)
    {
        Log.Error("{Summary}: {Details}", summary, details);

        var owner = Application.Current?.MainWindow;
        var dialog = new ErrorDialogWindow(summary, details);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private static string BuildDetails(string summary, Exception exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Summary: {summary}");
        sb.AppendLine($"TimestampUtc: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Type: {exception.GetType().FullName}");
        sb.AppendLine($"Message: {exception.Message}");

        if (exception.InnerException is not null)
        {
            sb.AppendLine($"InnerType: {exception.InnerException.GetType().FullName}");
            sb.AppendLine($"InnerMessage: {exception.InnerException.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("StackTrace:");
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }
}
