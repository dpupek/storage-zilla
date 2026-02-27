using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Services;
using AzureFilesSync.Desktop.ViewModels;
using AzureFilesSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AzureFilesSync.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\StorageZilla.Desktop.SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "Storage Zilla is already running.",
                "Storage Zilla",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _ownsSingleInstanceMutex = true;

        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync", "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "desktop-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Starting AzureFilesSync desktop app. Logs: {LogDirectory}", logDirectory);
        AttachUnhandledExceptionHandlers();

        if (InstallationConflictGuard.TryGetConflictMessage(out var installConflict))
        {
            Log.Warning("Installer mode conflict detected: {Conflict}", installConflict);
            ErrorDialog.ShowMessage("Installation conflict detected", installConflict);
            Shutdown();
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddAzureFilesSyncServices();
                services.AddSingleton<IConflictResolutionPromptService, WpfConflictResolutionPromptService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync().ConfigureAwait(true);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down AzureFilesSync desktop app.");
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void AttachUnhandledExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowErrorOnUiThread("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ShowErrorOnUiThread("Unhandled background task exception.", e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowErrorOnUiThread("Unhandled process exception.", ex);
            return;
        }

        ShowMessageOnUiThread("Unhandled process exception.", e.ExceptionObject?.ToString() ?? "Unknown unhandled exception object.");
    }

    private static void ShowErrorOnUiThread(string summary, Exception exception)
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null)
            {
                Log.Error(exception, "{Summary}", summary);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                ErrorDialog.Show(summary, exception);
                return;
            }

            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => ErrorDialog.Show(summary, exception)));
        }
        catch (Exception dispatchException)
        {
            Log.Error(dispatchException, "Failed to surface error dialog for summary {Summary}", summary);
            Log.Error(exception, "{Summary}", summary);
        }
    }

    private static void ShowMessageOnUiThread(string summary, string message)
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null)
            {
                Log.Error("{Summary}: {Message}", summary, message);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                ErrorDialog.ShowMessage(summary, message);
                return;
            }

            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => ErrorDialog.ShowMessage(summary, message)));
        }
        catch (Exception dispatchException)
        {
            Log.Error(dispatchException, "Failed to surface message dialog for summary {Summary}", summary);
            Log.Error("{Summary}: {Message}", summary, message);
        }
    }
}
