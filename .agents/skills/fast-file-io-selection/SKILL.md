---
name: fast-file-io-selection
description: Select the fastest file-reading method in PowerShell/.NET based on intent (raw bytes, full text, or streaming text). Use when performance matters, when comparing Get-Content vs .NET APIs, or when choosing file IO for large or medium files.
---

# Fast File IO Selection

## Overview

Pick the fastest file read approach by intent. Default to .NET `System.IO` APIs for speed and predictability. Avoid `Get-Content` for large files unless you need pipeline behavior.

## Decision Rules

1. **Need raw bytes (hashing, binary, exact byte length):**
   - Full read: `[System.IO.File]::ReadAllBytes($path)`
   - Streaming: `FileStream.Read(buffer)`
2. **Need full text in memory:**
   - Use `[System.IO.File]::ReadAllText($path)` for small/medium files.
   - Avoid `Get-Content` for large files.
3. **Need streaming text (line by line):**
   - Use `[System.IO.File]::ReadLines($path)`.
4. **Forced to use `Get-Content`:**
   - Prefer `Get-Content -ReadCount 1000` to reduce overhead.
5. **Do not use `rg` to benchmark raw read throughput:**
   - `rg` is optimized for search; process startup dominates.

## Recipes

```powershell
# Fastest full read (raw bytes)
[System.IO.File]::ReadAllBytes($path)

# Fastest full read (text)
[System.IO.File]::ReadAllText($path)

# Fastest streaming (raw bytes)
$buffer = New-Object byte[] 65536
$fs = [System.IO.File]::OpenRead($path)
try {
  while (($n = $fs.Read($buffer, 0, $buffer.Length)) -gt 0) { }
} finally {
  $fs.Dispose()
}

# Fastest streaming (text lines)
foreach ($line in [System.IO.File]::ReadLines($path)) { }

# If you must use Get-Content with streaming
Get-Content -LiteralPath $path -ReadCount 1000 | ForEach-Object { }
```

## Tail For Large Logs

Use `tail` when you only need the end of a very large log or you want to follow new entries. It avoids reading the full file and is the correct tool for "last N lines" and "follow" scenarios.

### When To Use Tail

- You only need the last N lines (recent errors, latest events).
- You need to follow a live log (`-f` / `-F` behavior).
- The file is large enough that full reads are wasteful.

### Prefer GNU tail (rotation-safe)

If available, prefer GNU `tail -F` because it follows filenames across log rotation:

```bash
tail -n 200 -F /path/to/log.log
```

### PowerShell fallback

```powershell
Get-Content -LiteralPath $path -Tail 200
Get-Content -LiteralPath $path -Tail 200 -Wait
```

### Find tail.exe ahead of time (Windows)

Use Everything CLI to locate `tail.exe`:

```powershell
es.exe -n 20 "tail.exe"
```

Common locations:
- `C:\Program Files\Git\usr\bin\tail.exe`
- `C:\Program Files (x86)\GnuWin32\bin\tail.exe`

If `tail` is installed but not on PATH, call it directly by full path.
