---
name: es-file-lookup
description: Use when the user asks to find or locate files by name or path on Windows (e.g., "find file X", "where is Y", "search for *.csproj"). Always use the Everything CLI (es.exe) instead of Get-ChildItem/dir/PowerShell recursion unless es.exe is missing or errors.
---

# ES File Lookup

## Overview

Use the Everything command-line interface (es.exe) for file name/path searches instead of PowerShell directory traversal. This is the default tool for locating files by name on Windows.

Key expectations:
- Everything must be installed and running for `es.exe` to return results.
- `es.exe` uses Everything search syntax.
- Prefer `es.exe` over `Get-ChildItem`, `dir`, or recursive PowerShell file searches when the user asks to find a file by name/path.

## Quick Start

- Basic name search:
  - `es.exe "filename.txt"`
- Limit results:
  - `es.exe -n 25 "filename.txt"`
- Match full path text (path + name):
  - `es.exe -p "E:\\Sandbox\\nexport-core-git-2\\NexportVirtualCampus.sln"`
- Scope to a folder:
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.sln"`
- Multiple extensions (regex):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" -regex ".*\\.(json|yml|yaml)$"`
- Regex search:
  - `es.exe -regex "^Nexport.*\\.sln$"`

## Recipes (es + rg)

- Find errors inside log files (content search after fast name lookup):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.log" | rg -i "error"`
- Find any TODOs inside matching files (scan controllers for tech debt):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*Controller*.cs" | rg "TODO"`
- Search for a specific string inside JSON configs (config key audit):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.json" | rg "\"ConnectionString\""`
- Find stack traces inside log/trace files (triage runtime failures):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.log" "*.trace" | rg -n "Exception|StackTrace"`
- Narrow to a feature area then search contents (targeted code spelunking):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2\\NexPortVirtualCampus" "*Scheduler*.cs" | rg -n "Quartz"`
- Find project references inside project files (dependency audit):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.csproj" | rg -n "<PackageReference"`
- Find obsolete usage across code (cleanup sweep):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.cs" | rg -n "Obsolete\\("`
- Search appsettings-like values across config files (environment drift checks):
  - `es.exe -path "E:\\Sandbox\\nexport-core-git-2" "*.config" "*.json" | rg -n "appsettings|connection"`

## Operational Guidance

1. Always use `es.exe` for name/path lookups (do not use Get-ChildItem/dir recursion unless es.exe is missing or fails).
2. If `es.exe` is not on PATH, fall back to a full path and retry:
   - `C:\\Program Files\\Everything\\es.exe`
   - `C:\\Program Files (x86)\\Everything\\es.exe`
3. Fallback check snippet:

```powershell
if (Get-Command es.exe -ErrorAction SilentlyContinue) {
  es.exe -n 25 "*.csproj"
} elseif (Test-Path "C:\\Program Files\\Everything\\es.exe") {
  & "C:\\Program Files\\Everything\\es.exe" -n 25 "*.csproj"
} elseif (Test-Path "C:\\Program Files (x86)\\Everything\\es.exe") {
  & "C:\\Program Files (x86)\\Everything\\es.exe" -n 25 "*.csproj"
} else {
  throw "es.exe not found; install Everything or provide its path."
}
```
4. If results are empty but the file should exist, remind the user that Everything must be running and that its index may still be building.
5. Use `-path` to scope results to a specific folder when a repo/workspace is provided.
6. Use `-p` when the user provides a path fragment and wants full-path matches.
7. Use `-n` to keep output manageable and align with user expectations.
8. Do not pass multiple `*.ext` globs to `es -path` expecting OR behavior; use `-regex` or run one extension per query.
9. Regex escaping in PowerShell: use `".*\\.$ext$"` (single backslash in the final regex). Over-escaping (e.g., `.*\\\\.$ext$`) yields zero matches.
10. Sanity-check Everything with a direct query (e.g., `es.exe -n 5 "NexportVirtualCampus.sln"`) if scoped searches return nothing.
11. For sizes/metadata, pipe paths into `Get-Item` after `es.exe` (don’t use recursive `Get-ChildItem`).

## Do Not Use

- Avoid `Get-ChildItem`, `dir`, or PowerShell recursion for name/path lookups unless the user explicitly asks for PowerShell.
- Avoid `rg`/`grep` when the task is "find a file by name" rather than "search file contents."
