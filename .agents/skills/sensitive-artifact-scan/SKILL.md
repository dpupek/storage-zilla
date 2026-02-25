---
name: sensitive-artifact-scan
description: Scan project artifacts for sensitive material (for example keys, PSKs, tokens, secrets) and produce remediation actions such as redaction, rotation, and storage-control follow-ups.
metadata:
  short-description: Sensitive artifact scanning
---

# Sensitive Artifact Scan

Use this skill when:
- Reviewing exported configs, logs, scripts, or documentation for security hygiene.
- Preparing artifacts for repo commit or evidence sharing.

## Workflow

1) Scope artifacts
- Identify files to scan (configs, logs, notes, attachments).
- Prioritize recently exported infrastructure/security artifacts.

2) Detect risky patterns
- Search for PSKs, private keys, tokens, passwords, connection strings, and API secrets.
- Flag encrypted secret blobs that still require controlled handling (for example `psksecret ENC`).

3) Classify findings
- High: plaintext secrets, private keys, active tokens.
- Medium: encrypted secret blobs, sensitive identifiers, internal topology details.
- Low: potential false positives needing owner confirmation.

4) Remediation plan
- Redact/remove from docs when possible.
- Rotate credentials/secrets if exposure scope is uncertain.
- Restrict artifact storage location and access if retention is required.

5) Evidence and follow-up
- Record what was scanned, findings summary, and remediation status.
- Track follow-up tasks to closure (rotation, cleanup, approvals).

## Expected outputs
- Findings table (file, pattern type, severity, action).
- Redaction/rotation task list.
- Commit-safe artifact status note.

