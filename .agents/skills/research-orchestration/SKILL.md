---
name: research-orchestration
description: Research workflow for when a question requires investigation or validation. Use to coordinate local repo context + Quick Memory, Microsoft Learn docs MCP (for Microsoft/Azure topics), Context7 MCP (for programming libraries), and web searches for up-to-date or niche info.
---

# Research Orchestration

Use this skill to structure investigations when you need to research a subject before responding.

## Workflow

1) **Check local context first**
   - Scan relevant `docs/` and repo files before external research.

2) **Query organizational memory**
   - Use `mcp__quick-memory__searchEntries` to find prior decisions, notes, or related work.
   - Use `mcp__quick-memory__listRecentEntries` if the topic is broad or you need a quick recap.

3) **Use the right first‑party source MCPs**
   - **Microsoft/Azure topics** → `mcp__microsoft_learn_docs__microsoft_docs_search` then `mcp__microsoft_learn_docs__microsoft_docs_fetch` for primary guidance and code.
   - **Programming libraries/frameworks** → `mcp__context7__resolve-library-id` then `mcp__context7__query-docs` for accurate, versioned docs.

4) **Web research (when needed)**
   - Use `web.run` for up‑to‑date, niche, or non‑Microsoft topics and to validate anything time‑sensitive.
   - Prefer authoritative sources and cite them in the response.

5) **Synthesize + record**
   - Summarize findings with sources and assumptions.
   - If this work updates ongoing decisions, log the result via Quick Memory (avoid secrets/PII).

## Environment validation branch (before recommending operational controls)

When recommendations depend on live admin connectivity (network/security changes):
- Validate local operator path assumptions first (adapter, route, DNS, access method).
- Confirm guidance matches the real environment state before finalizing rollout steps.
- Document any environment mismatch as a blocker or prerequisite in the recommendation.

## Telemetry schema check (required when planning technical enforcement)

Before recommending any enforcement control that depends on specific fields:
- List required fields up front (example: `srcmac`, device ID, user, group, tunneltype).
- Verify those fields exist in the available logs/exports.
- If fields are missing, mark the control as "not evidencable from current source" and recommend alternate telemetry.
- Separate what is proven vs inferred in the final recommendation.

## CMMC Mode (when SSP/gap-analysis work is requested)

If the request is about CMMC SSPs, scope, practices, or assessment-readiness language, use this source order:

1) Local repo artifacts first
- `docs/cmmc-program/**`
- `docs/cyber-security-reference/**`

2) First-party standards second
- DoD CMMC Scoping/Assessment Guides
- NIST SP 800-171

3) General web only if required
- Only to confirm current document links/versions or fill a missing first-party artifact.

When finishing CMMC research, provide a short "SSP research delta" block:

- What changed in the SSP/gap analysis from this research
- Which source(s) justify each change
- What remains open or interpretive

## Notes
- Keep results scoped and actionable; avoid speculative guidance.
- Favor primary sources; document gaps if authoritative guidance is missing.
- For Azure Automation issues involving variables/secrets, check Automation variable docs (complex types and encrypted variables can return non-string objects from `Get-AutomationVariable`).
