using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Services;
using AzureFilesSync.Infrastructure.Auth;
using AzureFilesSync.Infrastructure.Azure;
using AzureFilesSync.Infrastructure.Config;
using AzureFilesSync.Infrastructure.Local;
using AzureFilesSync.Infrastructure.Transfers;
using AzureFilesSync.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace AzureFilesSync.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureFilesSyncServices(this IServiceCollection services, Action<AzureClientOptions>? configure = null)
    {
        var options = new AzureClientOptions();
        configure?.Invoke(options);
        var updateOptions = new UpdateOptions();

        services.AddSingleton(options);
        services.AddSingleton(updateOptions);
        services.AddSingleton(sp =>
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(updateOptions.UserAgent, "1.0"));
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return httpClient;
        });
        services.AddSingleton<IAuthenticationService, InteractiveAuthenticationService>();
        services.AddSingleton<IAzureDiscoveryService, AzureDiscoveryService>();
        services.AddSingleton<IAzureFilesBrowserService, AzureFilesBrowserService>();
        services.AddSingleton<IRemoteFileOperationsService, RemoteFileOperationsService>();
        services.AddSingleton<IRemoteErrorInterpreter, RemoteErrorInterpreter>();
        services.AddSingleton<IRemoteCapabilityService, RemoteCapabilityService>();
        services.AddSingleton<IRemoteActionPolicyService, RemoteActionPolicyService>();
        services.AddSingleton<ILocalBrowserService, LocalBrowserService>();
        services.AddSingleton<ILocalFileOperationsService, LocalFileOperationsService>();
        services.AddSingleton<IConnectionProfileStore, FileConnectionProfileStore>();
        services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
        services.AddSingleton<ITransferConflictProbeService, TransferConflictProbeService>();
        services.AddSingleton<ITransferExecutor, AzureFileTransferExecutor>();
        services.AddSingleton<ITransferQueueService, TransferQueueService>();
        services.AddSingleton<IMirrorPlannerService, MirrorPlannerService>();
        services.AddSingleton<IMirrorExecutionService, MirrorExecutionService>();
        services.AddSingleton<IGitHubReleaseClient, GitHubReleaseClient>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();

        return services;
    }
}
