using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class MirrorPlannerService : IMirrorPlannerService
{
    private readonly ILocalBrowserService _localBrowserService;
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;

    public MirrorPlannerService(ILocalBrowserService localBrowserService, IAzureFilesBrowserService azureFilesBrowserService)
    {
        _localBrowserService = localBrowserService;
        _azureFilesBrowserService = azureFilesBrowserService;
    }

    public async Task<MirrorPlan> BuildPlanAsync(MirrorSpec spec, CancellationToken cancellationToken)
    {
        var localMap = await BuildLocalMapAsync(spec.LocalRoot, cancellationToken).ConfigureAwait(false);
        var remoteMap = await BuildRemoteMapAsync(spec.RemoteRoot, cancellationToken).ConfigureAwait(false);

        var allKeys = new HashSet<string>(localMap.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(remoteMap.Keys);

        var items = new List<MirrorPlanItem>();

        foreach (var key in allKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            localMap.TryGetValue(key, out var localItem);
            remoteMap.TryGetValue(key, out var remoteItem);

            if (spec.Direction == TransferDirection.Upload)
            {
                items.Add(BuildUploadPlanItem(spec, key, localItem, remoteItem));
            }
            else
            {
                items.Add(BuildDownloadPlanItem(spec, key, localItem, remoteItem));
            }
        }

        return new MirrorPlan(items);
    }

    private static MirrorPlanItem BuildUploadPlanItem(MirrorSpec spec, string key, LocalEntry? localItem, RemoteEntry? remoteItem)
    {
        if (localItem is not null && remoteItem is null)
        {
            return new MirrorPlanItem(MirrorActionType.Create, key, localItem.FullPath, AppendRemote(spec.RemoteRoot, key), localItem.Length, null, localItem.LastWriteTime, null);
        }

        if (localItem is null && remoteItem is not null)
        {
            var action = spec.IncludeDeletes ? MirrorActionType.Delete : MirrorActionType.Skip;
            return new MirrorPlanItem(action, key, null, AppendRemote(spec.RemoteRoot, key), null, remoteItem.Length, null, remoteItem.LastWriteTime);
        }

        if (localItem is not null && remoteItem is not null)
        {
            var needsUpdate = localItem.Length != remoteItem.Length || localItem.LastWriteTime > (remoteItem.LastWriteTime ?? DateTimeOffset.MinValue);
            var action = needsUpdate ? MirrorActionType.Update : MirrorActionType.Skip;
            return new MirrorPlanItem(action, key, localItem.FullPath, AppendRemote(spec.RemoteRoot, key), localItem.Length, remoteItem.Length, localItem.LastWriteTime, remoteItem.LastWriteTime);
        }

        throw new InvalidOperationException("Unreachable mirror planner state.");
    }

    private static MirrorPlanItem BuildDownloadPlanItem(MirrorSpec spec, string key, LocalEntry? localItem, RemoteEntry? remoteItem)
    {
        if (remoteItem is not null && localItem is null)
        {
            return new MirrorPlanItem(MirrorActionType.Create, key, Path.Combine(spec.LocalRoot, key), AppendRemote(spec.RemoteRoot, key), null, remoteItem.Length, null, remoteItem.LastWriteTime);
        }

        if (remoteItem is null && localItem is not null)
        {
            var action = spec.IncludeDeletes ? MirrorActionType.Delete : MirrorActionType.Skip;
            return new MirrorPlanItem(action, key, localItem.FullPath, null, localItem.Length, null, localItem.LastWriteTime, null);
        }

        if (remoteItem is not null && localItem is not null)
        {
            var needsUpdate = localItem.Length != remoteItem.Length || (remoteItem.LastWriteTime ?? DateTimeOffset.MinValue) > localItem.LastWriteTime;
            var action = needsUpdate ? MirrorActionType.Update : MirrorActionType.Skip;
            return new MirrorPlanItem(action, key, localItem.FullPath, AppendRemote(spec.RemoteRoot, key), localItem.Length, remoteItem.Length, localItem.LastWriteTime, remoteItem.LastWriteTime);
        }

        throw new InvalidOperationException("Unreachable mirror planner state.");
    }

    private static SharePath AppendRemote(SharePath root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var combined = string.IsNullOrEmpty(root.NormalizeRelativePath())
            ? normalized
            : $"{root.NormalizeRelativePath()}/{normalized}";
        return root with { RelativePath = combined };
    }

    private async Task<Dictionary<string, LocalEntry>> BuildLocalMapAsync(string root, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
        await TraverseLocalAsync(root, root, map, cancellationToken).ConfigureAwait(false);
        return map;
    }

    private async Task TraverseLocalAsync(string root, string currentPath, IDictionary<string, LocalEntry> map, CancellationToken cancellationToken)
    {
        var children = await _localBrowserService.ListDirectoryAsync(currentPath, cancellationToken).ConfigureAwait(false);
        foreach (var entry in children)
        {
            if (entry.IsDirectory)
            {
                await TraverseLocalAsync(root, entry.FullPath, map, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var relative = Path.GetRelativePath(root, entry.FullPath).Replace('\\', '/');
            map[relative] = entry;
        }
    }

    private async Task<Dictionary<string, RemoteEntry>> BuildRemoteMapAsync(SharePath root, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
        await TraverseRemoteAsync(root, string.Empty, map, cancellationToken).ConfigureAwait(false);
        return map;
    }

    private async Task TraverseRemoteAsync(SharePath root, string relativePrefix, IDictionary<string, RemoteEntry> map, CancellationToken cancellationToken)
    {
        var path = root with
        {
            RelativePath = string.IsNullOrEmpty(relativePrefix)
                ? root.NormalizeRelativePath()
                : string.IsNullOrEmpty(root.NormalizeRelativePath()) ? relativePrefix : $"{root.NormalizeRelativePath()}/{relativePrefix}"
        };

        var children = await _azureFilesBrowserService.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var entry in children)
        {
            var nextRelative = string.IsNullOrEmpty(relativePrefix) ? entry.Name : $"{relativePrefix}/{entry.Name}";
            if (entry.IsDirectory)
            {
                await TraverseRemoteAsync(root, nextRelative, map, cancellationToken).ConfigureAwait(false);
                continue;
            }

            map[nextRelative] = entry;
        }
    }
}
