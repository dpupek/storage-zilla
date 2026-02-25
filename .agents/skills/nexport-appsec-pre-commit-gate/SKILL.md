---
name: nexport-appsec-pre-commit-gate
description: Run a multi-stack application security review on staged changes before commit (.NET, Python, PHP, Node.js), including dependency vulnerabilities, code scanning, and secrets checks.
---

# Skill: nexport-appsec-pre-commit-gate

## When to use
- User asks for a pre-commit security review.
- User asks to scan latest changes for vulnerabilities or secrets.
- User wants one workflow covering .NET, Python, PHP, and Node.js.

## Default policy
- Mode: `warn-only`
- Secret scan scope: `staged`
- Latest changes source: staged diff (`git diff --name-only --cached`)

## Guardrails
- Never print raw secrets in output.
- Never auto-commit fixes.
- Prefer staged-file scope for speed and relevance.
- If a required tool is missing, report install guidance and continue with remaining checks.

## Inputs
Required:
- Repository root with git metadata.

Optional:
- `references/appsec-gate.config.template.json` copied to a local config file.
- CI handoff settings for Azure DevOps.

## Workflow

1) Preflight
- Confirm repo root and staged file set.
- Detect ecosystems by manifests and staged extensions:
  - .NET: `*.sln`, `*.csproj`, `packages.lock.json`
  - Node.js: `package.json`, `package-lock.json`, `npm-shrinkwrap.json`
  - Python: `requirements*.txt`, `pyproject.toml`, `poetry.lock`, `Pipfile.lock`
  - PHP: `composer.json`, `composer.lock`

2) Dependency vulnerability checks (run for detected ecosystems)
- .NET:
  - `dotnet package list --vulnerable --include-transitive --format json`
  - fallback: `dotnet list package --vulnerable --include-transitive --format json`
- Node.js:
  - `npm audit --json`
- Python:
  - `pip-audit`
- PHP:
  - `composer audit --format=json`

3) Code vulnerability checks on latest changes
- Build staged file list for supported code extensions.
- Run Semgrep on staged files only:
  - rulesets: `p/owasp-top-ten` plus language rules.
- Capture file, line, rule id, severity, and message.

4) Secret scan on latest changes
- Run Gitleaks in staged scope.
- Record only redacted metadata (rule id, file, line/fingerprint).

5) Normalize and summarize
- Produce unified structure:
  - `dependencies[]`, `code[]`, `secrets[]`
  - severity totals by category
  - policy decision (`pass`, `warn`, `block`)
- Emit remediation commands per finding category.

6) Apply policy
- `warn-only`: report findings, do not block commit.
- `block-high-critical`: block on high/critical findings.
- `block-any`: block on any finding.

7) Optional Azure DevOps handoff
- If CI handoff enabled, offer or run:
  - `az devops configure --defaults organization=<org> project=<project>`
  - `az pipelines run --id <pipelineId> --branch <branch> --variables AppSecGate=true`

## Output requirements
- Preflight summary.
- Commands executed (sanitized).
- Consolidated security summary by category/severity.
- Policy decision and next actions.
- Optional CI handoff command/result.

## References
- `references/appsec-gate.config.template.json`
- `references/security-report-schema.md`
- `references/security-report.example.json`
- `references/tool-install-and-verify.md`
- `scripts/run-appsec-gate.ps1`
- `scripts/run-appsec-gate.sh`

## Done criteria
- Staged changes scanned for dependencies/code/secrets where tooling is available.
- Unified report produced with clear severity rollup.
- Policy outcome clearly reported.
- Remediation steps provided.
