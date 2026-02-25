using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class MirrorExecutionService : IMirrorExecutionService
{
    private readonly ITransferQueueService _transferQueueService;

    public MirrorExecutionService(ITransferQueueService transferQueueService)
    {
        _transferQueueService = transferQueueService;
    }

    public Task<MirrorExecutionResult> ExecuteAsync(MirrorPlan plan, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var succeeded = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Action is MirrorActionType.Skip or MirrorActionType.Delete)
            {
                continue;
            }

            if (item.LocalPath is null || item.RemotePath is null)
            {
                errors.Add($"Invalid mirror item for {item.RelativePath}.");
                continue;
            }

            var direction = File.Exists(item.LocalPath) ? TransferDirection.Upload : TransferDirection.Download;
            var request = new TransferRequest(direction, item.LocalPath, item.RemotePath);
            _transferQueueService.Enqueue(request);
            succeeded++;
        }

        return Task.FromResult(new MirrorExecutionResult(succeeded, errors.Count, errors));
    }
}
