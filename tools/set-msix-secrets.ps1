param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $false)]
    [string]$Repo = "dpupek/storage-zilla",

    [Parameter(Mandatory = $false)]
    [string]$Publisher
)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required."
}

if (-not (Test-Path -LiteralPath $PfxPath)) {
    throw "PFX file not found: $PfxPath"
}

$resolvedPfx = (Resolve-Path -LiteralPath $PfxPath).Path
$secure = Read-Host "Enter PFX export password" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrWhiteSpace($password)) {
    throw "PFX password cannot be empty."
}

try {
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedPfx, $password, $flags)
    if (-not $cert.HasPrivateKey) {
        throw "PFX does not include a private key."
    }
    if ($cert.NotAfter -lt (Get-Date)) {
        throw "PFX certificate is expired: $($cert.NotAfter.ToString('u'))."
    }
    Write-Host "Validated certificate: $($cert.Subject) ($($cert.Thumbprint))"
    $cert.Dispose()
} catch {
    throw "Unable to open certificate with supplied password. Export a fresh PFX and retry. Details: $($_.Exception.Message)"
}

$certBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($resolvedPfx))

$certBase64 | gh secret set MSIX_CERT_BASE64 --repo $Repo
$password | gh secret set MSIX_CERT_PASSWORD --repo $Repo

if (-not [string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher | gh secret set MSIX_PUBLISHER --repo $Repo
}

Write-Host "Secrets updated successfully for $Repo."
