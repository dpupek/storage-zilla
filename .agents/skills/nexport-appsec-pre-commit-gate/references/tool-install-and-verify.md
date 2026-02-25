# Tool Install and Verify

Install these tools when needed:

- .NET SDK:
  - Verify: `dotnet --info`
- Node.js/npm:
  - Verify: `node -v` and `npm -v`
- Python + pip-audit:
  - Install: `python -m pip install pip-audit`
  - Verify: `pip-audit --version`
- PHP + Composer:
  - Verify: `php -v` and `composer --version`
- Semgrep:
  - Install: `pip install semgrep` (or platform package)
  - Verify: `semgrep --version`
- Gitleaks:
  - Verify: `gitleaks version`
- Azure CLI (optional CI handoff):
  - Verify: `az version`

Quick smoke checks:

- .NET: `dotnet package list --vulnerable --include-transitive --format json`
- Node: `npm audit --json`
- Python: `pip-audit`
- PHP: `composer audit --format=json`
- Semgrep: `semgrep scan --config p/owasp-top-ten --json --error`
- Gitleaks staged: `gitleaks git --staged --redact`
