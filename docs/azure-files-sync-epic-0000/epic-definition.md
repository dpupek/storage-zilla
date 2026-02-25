# Epic Definition

## Big Idea
Build a Windows desktop FTP-style client for Azure File Shares with side-by-side browsing, queued transfers, and safe mirror sync.

## Success Criteria
- [ ] User signs in with fully interactive Entra login.
- [ ] User can browse Subscriptions -> Storage Accounts -> File Shares.
- [ ] User can upload/download with queue visibility and retry/cancel.
- [ ] User can generate mirror plan and explicitly confirm delete operations.

## Decisions
- Platform: Windows-only WPF (.NET 10)
- Auth: Interactive only
- Scope: Local <-> Azure Files
