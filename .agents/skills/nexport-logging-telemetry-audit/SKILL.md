---
name: nexport-logging-telemetry-audit
description: Logging, telemetry, and auditing guidance for NexPort. Use when discussing logging config, Serilog/log4net usage, audit logs, app logs, telemetry events, observability dashboards, or deciding where to record a system/user event.
---

# NexPort Logging, Telemetry, and Audit

## Overview

Choose the correct log/audit/telemetry mechanism, apply the right naming conventions, and wire changes into the logging configuration and observability surfaces.

## Decision Guide

Use this quick mapping to decide where to log:

- **Audit log (user/tenant history, compliance)** → persist as a domain audit record or `LogEntry`.
- **App log (debug/diagnostic, error tracing)** → standard log pipeline (Serilog/log4net).
- **Telemetry log (analytics/metrics, dashboards)** → structured Serilog event with stable `EventId`.

Treat events visible to admins as historical activity as audit. Treat troubleshooting data as app log. Treat counts/trends/dashboard data as telemetry.

## Audit Logs (persistent, compliance/history)

Use audit logging when the action must remain visible later regardless of log retention.

Approaches:

- **Generic activity log**: `Nexport.Models.Logging.LogEntry.Log(...)` with a `LoggingAction` when the action is user/org centric and should appear in activity log UIs.
- **Domain-specific audit entities**: prefer a dedicated entity (e.g., `ExternalIdentityProviderAuditEntry`) when you need richer payloads or a dedicated audit screen.

Guidelines:

- Avoid PII beyond what is required for the audit record.
- Use a clear action enum/value and consistent message template.
- Keep audit writes in the same transaction as the change.

## App Logs (diagnostics)

Use app logs for debugging, exception tracking, and operational visibility.

Approaches:

- Use `ILogger` (preferred) or log4net where existing. Serilog is configured as the sink.
- Respect runtime config: global minimum + namespace overrides in `NexPortLogConfigViewModel`.
- Ensure log statements include identifiers (`OrgId`, `UserId`, `ProviderId`) but avoid secrets/tokens.

## Telemetry Logs (structured analytics)

Use telemetry logs for event counts and operational metrics that power Observability dashboards.

Approach:

- Emit a structured Serilog event with a stable `EventId` value.
- Use `Log.ForContext("EventId", "...")` and add structured properties for filtering/aggregation.
- Keep EventId naming consistent and prefix-based (e.g., `ExternalLogin.ProviderSelected`, `TenantUsageSnapshot.Start`).

Recommended properties:

- `OrganizationId`, `ProviderId`, `ConnectorId`, `UserId` (if needed and allowed)
- `Surface` / `FeatureArea` / `Scenario`
- `Outcome` or `Status` for success/failure

Do not include access tokens, secrets, or raw claims.

## Logging Config + Observability

Reference logging configuration from `NexPortLogConfigViewModel` and apply changes via `LogConfigReloader`.

When adding telemetry:

- Add a stable `EventId` string (const class).
- If new telemetry category should be tunable, add a filter concept in logging config (EventId prefix + level).
- If it should surface in Observability, ensure the log query services can recognize the new `EventId` pattern.

## Naming Conventions

- **Telemetry EventId**: `Area.Action` (e.g., `ExternalLogin.ProviderSelected`).
- **Log messages**: keep short; rely on structured properties for details.
- **Audit actions**: match `LoggingAction` or a domain-specific enum.

## Minimal Example (Telemetry)

```csharp
public static class ExternalLoginLogEvents
{
    public const string ProviderSelected = "ExternalLogin.ProviderSelected";
}

Log.ForContext("EventId", ExternalLoginLogEvents.ProviderSelected)
    .ForContext("ProviderId", provider.Id)
    .ForContext("OrganizationId", organization.Id)
    .Information("External login provider selected.");
```
