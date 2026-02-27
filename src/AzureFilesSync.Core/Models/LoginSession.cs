namespace AzureFilesSync.Core.Models;

public sealed record LoginSession(
    bool IsAuthenticated,
    string DisplayName,
    string TenantId,
    string AuthMode = "Unknown",
    bool UsedFallback = false);
