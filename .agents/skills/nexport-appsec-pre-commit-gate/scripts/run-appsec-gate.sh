#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-warn-only}"
echo "[appsec-gate] mode=${MODE}"

STAGED_COUNT=$(git diff --name-only --cached | wc -l | tr -d ' ')
if [[ "${STAGED_COUNT}" == "0" ]]; then
  echo "[appsec-gate] no staged changes detected"
  exit 0
fi

echo "[appsec-gate] staged file count: ${STAGED_COUNT}"

echo "[appsec-gate] dotnet: dotnet package list --vulnerable --include-transitive --format json"
echo "[appsec-gate] node: npm audit --json"
echo "[appsec-gate] python: pip-audit"
echo "[appsec-gate] php: composer audit --format=json"
echo "[appsec-gate] sast: semgrep scan --config p/owasp-top-ten --json"
echo "[appsec-gate] secrets: gitleaks git --staged --redact"

# Policy handling is intentionally left to skill orchestrator/report parser.
exit 0
