namespace AzureFilesSync.Core.Models;

public sealed record LoginSession(bool IsAuthenticated, string DisplayName, string TenantId);
