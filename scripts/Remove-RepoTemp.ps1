[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string[]] $RelativePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tmp'))
$scratchPrefix = $scratchRoot + [IO.Path]::DirectorySeparatorChar

function Assert-NoReparsePoint([string] $LiteralPath) {
    $item = Get-Item -LiteralPath $LiteralPath -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Scratch cleanup refuses reparse point: $LiteralPath"
    }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $LiteralPath -Force) {
            Assert-NoReparsePoint $child.FullName
        }
    }
}

# Validate every target before deleting any target.
$targets = foreach ($relative in $RelativePath) {
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or $relative -match '[:*?\[\]]' -or
        ($relative -split '[/\\]' | Where-Object { $_ -match '[. ]$' })) {
        throw "Supply a literal relative path inside .tmp: $relative"
    }
    $target = [IO.Path]::GetFullPath((Join-Path $scratchRoot $relative))
    if (-not $target.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Scratch cleanup target must be a descendant of $scratchRoot"
    }
    # Check ancestors as well as the target, before traversing its contents.
    $ancestor = $target
    while ($ancestor -and $ancestor.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Scratch cleanup refuses reparse point: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $target) {
        Assert-NoReparsePoint $target
        $target
    }
}
foreach ($target in $targets) {
    if ($PSCmdlet.ShouldProcess($target, 'Remove repository scratch artifact')) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
