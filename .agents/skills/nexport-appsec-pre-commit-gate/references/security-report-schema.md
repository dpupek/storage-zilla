# Security Report Schema

This skill emits a normalized report shape so downstream tools can parse results consistently.

## Top-level object

- `toolRun` (object)
  - `timestampUtc` (string, ISO 8601)
  - `repoPath` (string)
  - `stagedOnly` (boolean)
- `policy` (object)
  - `mode` (`warn-only` | `block-high-critical` | `block-any`)
  - `decision` (`pass` | `warn` | `block`)
- `ecosystemsDetected` (array<string>)
- `findings` (object)
  - `dependencies` (array<object>)
    - `ecosystem` (string)
    - `package` (string)
    - `version` (string)
    - `advisoryId` (string)
    - `severity` (string)
    - `fixedVersion` (string)
    - `file` (string)
  - `code` (array<object>)
    - `tool` (string)
    - `ruleId` (string)
    - `severity` (string)
    - `message` (string)
    - `file` (string)
    - `line` (number)
  - `secrets` (array<object>)
    - `tool` (string)
    - `detector` (string)
    - `severity` (string)
    - `file` (string)
    - `line` (number)
    - `fingerprint` (string)
- `summary` (object)
  - `totalsBySeverity` (object)
  - `totalsByCategory` (object)
  - `recommendedAction` (string)
- `commandsExecuted` (array<string>)

## Severity normalization

Normalize all tools to this set:
- `critical`
- `high`
- `medium`
- `low`
- `info`
