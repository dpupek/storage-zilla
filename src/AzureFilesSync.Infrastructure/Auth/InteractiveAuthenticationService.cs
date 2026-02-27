using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;
using System.Diagnostics;

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
        var credential = CreateCredential(preferBroker: true);
        var authMode = "WAM";
        var usedFallback = false;
        AuthenticationRecord authRecord;
        try
        {
            authRecord = await credential.AuthenticateAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex) when (ShouldFallbackToSystemBrowser(ex))
        {
            credential = CreateCredential(preferBroker: false);
            authMode = "SystemBrowser";
            usedFallback = true;
            authRecord = await credential.AuthenticateAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                cancellationToken).ConfigureAwait(false);
        }

        _credential = credential;
        _current = new LoginSession(
            true,
            authRecord.Username ?? "Signed in",
            authRecord.TenantId,
            authMode,
            usedFallback);
        return _current;
    }

    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        _credential = null;
        _current = new LoginSession(false, "Not signed in", string.Empty);
        return Task.CompletedTask;
    }

    public TokenCredential GetCredential() => _credential ?? throw new InvalidOperationException("User must sign in before accessing Azure resources.");

    private InteractiveBrowserCredential CreateCredential(bool preferBroker)
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync");
        Directory.CreateDirectory(cachePath);

        TokenCachePersistenceOptions cacheOptions = new()
        {
            Name = "AzureFilesSyncTokenCache",
            UnsafeAllowUnencryptedStorage = false
        };

        if (preferBroker)
        {
            var parentWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (parentWindowHandle != IntPtr.Zero)
            {
                var brokerOptions = new InteractiveBrowserCredentialBrokerOptions(parentWindowHandle)
                {
                    TenantId = _options.TenantId,
                    TokenCachePersistenceOptions = cacheOptions
                };

                return new InteractiveBrowserCredential(brokerOptions);
            }
        }

        var interactiveOptions = new InteractiveBrowserCredentialOptions
        {
            TenantId = _options.TenantId,
            RedirectUri = new Uri(_options.RedirectUri),
            BrowserCustomization = new BrowserCustomizationOptions
            {
                SuccessMessage = "<html><body><h2>Sign-in complete.</h2><p>You can close this tab and return to Storage Zilla.</p></body></html>"
            },
            TokenCachePersistenceOptions = cacheOptions
        };

        return new InteractiveBrowserCredential(interactiveOptions);
    }

    private static bool ShouldFallbackToSystemBrowser(AuthenticationFailedException ex)
    {
        if (ex.InnerException is not null && ShouldFallbackToSystemBrowser(ex.InnerException))
        {
            return true;
        }

        return ShouldFallbackToSystemBrowser((Exception)ex);
    }

    private static bool ShouldFallbackToSystemBrowser(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("window_handle_required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("A window handle must be configured", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException is not null && ShouldFallbackToSystemBrowser(ex.InnerException);
    }
}
