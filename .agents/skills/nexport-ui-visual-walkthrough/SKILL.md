---
name: nexport-ui-visual-walkthrough
description: "Run a manual UI visual walkthrough/check. Build first with roslyn_code_navigator, start the site with roslyn_code_navigator StartAspNet, then validate pages/interactions with Playwright MCP when available."
---

# NexPort UI Visual Walkthrough

## When to use
- User asks for a visual UI check, walkthrough, smoke validation, or visual regression review.
- User asks to inspect rendered output directly in browser.

## Required sequence
1. Build the app first.
2. Start the app second.
3. Run browser walkthrough third.
4. Stop the app session at the end.

Do not skip this order unless the user explicitly says the site is already running and gives the URL.

## Step 1: Build (Roslyn MCP first)
- Preferred tool: `roslyn_code_navigator.BuildSolution`.
- Preferred target: repo solution `.sln` (not `.slnf`).
- Use concise output args when available:
  - `-clp:ErrorsOnly`
  - `/p:SkipWebContentCopy=true` (when relevant to test/build output size)
- If BuildSolution is unavailable/failing due to environment/tooling, fall back to Windows `dotnet.exe` build.

## Step 2: Start site (Roslyn MCP first)
- Preferred tool: `roslyn_code_navigator.StartAspNet`.
- Set:
  - `projectPath` to the web project `.csproj`
  - `launchProfile` to a profile suitable for UI checks
  - `logToFile: true`
- Capture and retain:
  - session `token`
  - bound `urls`
  - `logFilePath`
- Resolve the real endpoint before Playwright checks:
  - `StartAspNet.urls` may be overridden by app configuration.
  - Check startup logs for `Overriding address(es)` and use actual listener endpoint.
  - If needed, inspect child process socket bindings to confirm where the app listens.
- Confirm readiness before visual checks:
  - poll URL for HTTP 200 or expected page content
  - review `GetAspNetOutput` for startup errors

## Step 3: Visual walkthrough (Playwright MCP)
- Preferred tool: Playwright MCP (`playwright_chrome`) if available.
- Navigate directly to target URLs (avoid browser back/forward for app state-sensitive flows).
- For CSS-regression investigations, force one cache-busting load (for example `?cb=2`) before final checks.
- Verify at least:
  - page loads (no 500/error shell)
  - expected layout/theme loads
  - key controls render and are interactable
  - no obvious CSS collisions (text decoration, icon/background bleed, alignment issues)
- Run a CSS collision checklist for embedded Swagger pages:
  - `#swagger-ui .information-container a` should not inherit sprite backgrounds.
  - `#swagger-ui .arrow` should not receive portal arrow background images.
  - `#swagger-ui .tab` should not receive portal tab styling.
  - `#swagger-ui .opblock-summary-path__deprecated` line-through is expected for deprecated APIs.
- Capture evidence:
  - snapshots/screenshots for key states
  - computed-style evidence (`getComputedStyle`) for suspected collisions
  - console errors/warnings when relevant
- If Playwright MCP is unavailable, report that and provide fallback manual verification steps.

## Step 4: Teardown
- Stop the ASP.NET session using the Roslyn token (`StopAspNet`).
- If stop fails, note that process already exited or provide remediation.

## Reporting format
Include in the response:
- What was checked (pages and interactions).
- What failed/passed.
- Evidence summary (screenshots/snapshots/log markers).
- Exact files changed (if any).
- Commands/tools used.

## Fallback rules
- If Roslyn start/build fails, fall back to Windows `dotnet.exe` commands.
- If Playwright MCP times out, retry once after readiness re-check.
- If route checks fail on the requested URL, re-resolve actual bound endpoint from logs before retrying.
- If still blocked, return a precise blocker summary with the next best actionable step.
