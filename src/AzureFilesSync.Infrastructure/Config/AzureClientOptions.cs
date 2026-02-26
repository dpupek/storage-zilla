namespace AzureFilesSync.Infrastructure.Config;

public sealed class AzureClientOptions
{
    public string TenantId { get; set; } = "organizations";
    public string RedirectUri { get; set; } = "http://localhost";
    public int TransferChunkSizeBytes { get; set; } = 4 * 1024 * 1024;
    public int TransferConcurrency { get; set; } = 4;
    public int TransferMaxBytesPerSecond { get; set; } = 0;
    public int TransferRetryAttempts { get; set; } = 4;
    public int TransferRetryBaseDelayMs { get; set; } = 250;
}
