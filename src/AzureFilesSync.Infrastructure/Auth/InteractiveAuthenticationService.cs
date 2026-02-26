using Azure.Core;
using Azure.Identity;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;

namespace AzureFilesSync.Infrastructure.Auth;

public sealed class InteractiveAuthenticationService : IAuthenticationService
{
    private readonly AzureClientOptions _options;
    private InteractiveBrowserCredential? _credential;
    private LoginSession _current = new(false, "Not signed in", string.Empty);

    public InteractiveAuthenticationService(AzureClientOptions options)
    {
        _options = options;
    }

    public async Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken)
    {
        var credential = CreateCredential();
        var authRecord = await credential.AuthenticateAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken).ConfigureAwait(false);

        _credential = credential;
        _current = new LoginSession(true, authRecord.Username ?? "Signed in", authRecord.TenantId);
        return _current;
    }

    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        _credential = null;
        _current = new LoginSession(false, "Not signed in", string.Empty);
        return Task.CompletedTask;
    }

    public TokenCredential GetCredential() => _credential ?? throw new InvalidOperationException("User must sign in before accessing Azure resources.");

    private InteractiveBrowserCredential CreateCredential()
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync");
        Directory.CreateDirectory(cachePath);

        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = _options.TenantId,
            RedirectUri = new Uri(_options.RedirectUri),
            BrowserCustomization = new BrowserCustomizationOptions
            {
                UseEmbeddedWebView = true,
                SuccessMessage = "<html><body><h2>Sign-in complete.</h2><p>You can close this window and return to Storage Zilla.</p></body></html>"
            },
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "AzureFilesSyncTokenCache",
                UnsafeAllowUnencryptedStorage = false
            }
        };

        return new InteractiveBrowserCredential(options);
    }
}
