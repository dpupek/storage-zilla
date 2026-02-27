# Stakeholder Summary

## Admins
- Uses Entra interactive login and RBAC instead of distributing secrets.
- Delete confirmation helps reduce accidental data loss.
- Auth path is more supportable across environments with WAM-first sign-in and automatic browser fallback.

## Developers
- FTP-style side-by-side browsing for local and Azure Files.
- Queue-based uploads/downloads with clear progress and retry behavior.
- Dual installer outputs per release: signed MSIX (auto-update path) and unsigned MSI (manual/internal deployment path).
- Saved profile recovery no longer fails the entire sign-in when one Azure Files endpoint is temporarily unavailable.

## External Stakeholders
- Faster onboarding to Azure Files with familiar desktop interaction model.
- Reduced operational friction compared to custom scripts.
- Improved installer experience (license screen, install-location UI, and Start Menu integration) for non-technical users.
