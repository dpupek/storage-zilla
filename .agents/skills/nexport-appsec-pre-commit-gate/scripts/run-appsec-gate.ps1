param(
    [ValidateSet("warn-only","block-high-critical","block-any")]
    [string]$Mode = "warn-only"
)

$ErrorActionPreference = "Continue"

Write-Host "[appsec-gate] mode=$Mode"

$staged = git diff --name-only --cached
if ([string]::IsNullOrWhiteSpace(($staged -join ""))) {
    Write-Host "[appsec-gate] no staged changes detected"
    exit 0
}

Write-Host "[appsec-gate] staged file count: $($staged.Count)"

# Wrapper helper: this script intentionally prints canonical commands.
Write-Host "[appsec-gate] dotnet: dotnet package list --vulnerable --include-transitive --format json"
Write-Host "[appsec-gate] node: npm audit --json"
Write-Host "[appsec-gate] python: pip-audit"
Write-Host "[appsec-gate] php: composer audit --format=json"
Write-Host "[appsec-gate] sast: semgrep scan --config p/owasp-top-ten --json"
Write-Host "[appsec-gate] secrets: gitleaks git --staged --redact"

# Policy handling is intentionally left to skill orchestrator/report parser.
exit 0
