---
name: nexport-epic-shaping-decomposition
description: "NexPort skill: epic shaping and decomposition with roadmap composition + optional FogBugz epic and child cases."
---

# Skill: nexport-epic-shaping-decomposition

## When to use
- Starting or continuing a major epic.
- You need a structured plan, roadmap, or stakeholder artifacts.

## Checklist
- Ask for epic case number and short slug. If none exists, offer to create a **FogBugz epic** case and ask for the **milestone** before creating it.
- Ask whether to follow the epic blueprint (new epics use `docs/active-projects/<short-name>-<case-number>/`; legacy epics under `docs/<short-name>-<case-number>/` can remain).
- Create/update these artifacts:
  - `epic-definition.md` (big idea, success criteria, non-goals)
  - `workflows.md` (personas, steps, linked cases)
  - `baseline.md` (current behavior + constraints)
  - `edge-cases.md` (risks + mitigations)
  - `crcs.md` (classes/services + responsibilities/invariants)
  - `stakeholder-summary.md` (value + demo script)
  - `roadmap.md` (phases, decisions, user stories, validation)
- Roadmap requirements:
  - **Phase 0** includes **overall decision requirements** (checkboxes).
  - **Every phase** includes its own **Decisions Needed** subsection.
  - Each phase has **at least two sections** (e.g., Data Models + Services, UI + Permissions).
  - **User Stories** include sub-tasks and case numbers (or “no case yet”).
  - Each phase ends with **Validation** (commands/results or “not run”).
- Baseline vs edge cases:
  - Baseline = current behavior + constraints.
  - Edge cases = risks + performance + security + concurrency concerns.
- Offer to create **enhancement cases** for roadmap phases/stories/tasks; suggest a few stable candidates, but make it optional.

## References
- patterns/ (process + architecture patterns that influence workflows/CRCs)
- FogBugz epic case text (goal, constraints, related cases)
- Quick-memory entries tagged with the epic/case (decisions, pitfalls, test commands)
- docs/*/roadmap.md (for examples)
