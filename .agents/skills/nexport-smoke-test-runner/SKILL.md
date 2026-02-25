---
name: nexport-smoke-test-runner
description: "Run NexPort smoke tests with a consistent prompt that always asks whether UI or unit smoke tests are desired; prefers Roslyn MCP StartTest/TestSolution when available and falls back to Windows dotnet.exe or CLI commands. Use when asked to run smoke tests, tag smoke tests, or validate a quick sanity pass."
---

# NexPort Smoke Test Runner

## Workflow
- Always ask: "UI smoke or unit smoke?" if the user did not specify.
- If UI smoke is requested, explicitly consult the UI test runner skills (`nexport-ui-tests-runner` and `nexport-ui-tests-startup-troubleshooting`) before starting the run to align on startup sequencing, readiness checks, and failure handling.
- If smoke failures are controller/session-flow related, consult `.codex/skills/nexport-nhibernate-aaaa-session-pattern/SKILL.md` for AAAA/session-boundary checks.
- For UI smoke, include preflight bind verification:
  - check startup logs for URL override warnings
  - verify actual bound endpoint before liveness probing
  - if manual probe remains inconsistent, proceed with targeted smoke and trust test outcomes.
- Prefer Roslyn MCP tools if available:
  - Use `roslyn_code_navigator.StartTest` (async) for long runs.
  - Use `roslyn_code_navigator.TestSolution` for shorter runs.
- If Roslyn MCP fails or is unavailable, fall back to Windows `dotnet.exe` (WSL `/mnt` guidance).

## Commands
- **UI smoke**:
  - Roslyn: `TestSolution` or `StartTest` on `/mnt/.../NexPort.UiTests/NexPort.UiTests.csproj` with `--filter "Category=UiSmoke"`.
  - CLI fallback: `"$NEXPORT_WINDOTNETdotnet.exe" test E:\\sandbox\\nexport-core-git-2\\NexPort.UiTests\\NexPort.UiTests.csproj -c Debug --filter "Category=UiSmoke"`

- **Unit smoke**:
  - Roslyn: `TestSolution` or `StartTest` on `/mnt/.../NexPort.Tests/Nexport.Tests.csproj` with `--filter "Category=Smoke"`.
  - CLI fallback: `"$NEXPORT_WINDOTNETdotnet.exe" test E:\\sandbox\\nexport-core-git-2\\NexPort.Tests\\Nexport.Tests.csproj -c Debug --filter "Category=Smoke"`

## Notes
- Never run the full suite.
- If SDK version is needed, prefer the available 8.0.x version from Roslyn MCP runners.
- Report test results with failures summarized first.
- Treat `No test matches the given testcase filter` as an invalid run for the requested scope; retry with a broader but intentional filter (for example class-name filter) and report discovered test count.
- For UI smoke runs, confirm the UiTests site has cleared the startup splash before running `Category=UiSmoke`.
- Always emit TRX for UI smoke runs (for post-failure triage and case comments), e.g. `--logger \"trx;LogFileName=ui-smoke.trx\"`.
- Troubleshooting timeouts (NHibernate/SQL):
  - Look for `Execution Timeout Expired` during group/tree updates (`UpdatingLeftright`).
  - Avoid nested sessions/transactions in Arrange; split Unit of Work across separate sessions with explicit commits.
  - Create orgs/groups in their own session/transaction, then open a fresh session for dependent data setup.
  - If failures persist, inspect for nested transaction usage in helpers (`BeginTransaction()` inside helper calls).

## References
- .codex/skills/nexport-nhibernate-aaaa-session-pattern/SKILL.md
