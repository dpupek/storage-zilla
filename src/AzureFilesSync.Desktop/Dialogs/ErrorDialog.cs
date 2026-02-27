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
        ShowDialogOnUiThread(summary, details);
    }

    public static void ShowMessage(string summary, string details)
    {
        Log.Error("{Summary}: {Details}", summary, details);
        ShowDialogOnUiThread(summary, details);
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

    private static void ShowDialogOnUiThread(string summary, string details)
    {
        try
        {
            var app = Application.Current;
            var dispatcher = app?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                ShowDialogInternal(summary, details);
                return;
            }

            dispatcher.Invoke(() => ShowDialogInternal(summary, details));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to display error dialog. Summary={Summary}", summary);
        }
    }

    private static void ShowDialogInternal(string summary, string details)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new ErrorDialogWindow(summary, details);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }
}
