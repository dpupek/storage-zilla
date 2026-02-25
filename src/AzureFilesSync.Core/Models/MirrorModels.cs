namespace AzureFilesSync.Core.Models;

public enum MirrorActionType
{
    Create,
    Update,
    Delete,
    Skip
}

public sealed record MirrorSpec(
    TransferDirection Direction,
    string LocalRoot,
    SharePath RemoteRoot,
    bool IncludeDeletes);

public sealed record MirrorPlanItem(
    MirrorActionType Action,
    string RelativePath,
    string? LocalPath,
    SharePath? RemotePath,
    long? LocalLength,
    long? RemoteLength,
    DateTimeOffset? LocalLastWriteUtc,
    DateTimeOffset? RemoteLastWriteUtc);

public sealed record MirrorPlan(IReadOnlyList<MirrorPlanItem> Items)
{
    public int CreateCount => Items.Count(x => x.Action == MirrorActionType.Create);
    public int UpdateCount => Items.Count(x => x.Action == MirrorActionType.Update);
    public int DeleteCount => Items.Count(x => x.Action == MirrorActionType.Delete);
}

public sealed record MirrorExecutionResult(int Succeeded, int Failed, IReadOnlyList<string> Errors);
