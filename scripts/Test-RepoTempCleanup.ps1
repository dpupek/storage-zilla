$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testName = 'cleanup-tests-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repoRoot ".tmp/$testName"
$cleanup = Join-Path $PSScriptRoot 'Remove-RepoTemp.ps1'
$null = New-Item -ItemType Directory -Path "$testRoot/keep" -Force
Set-Content -LiteralPath "$testRoot/keep/marker.txt" -Value 'preserve'

function Assert-Rejected([string] $Path) {
    $rejected = $false
    try { & $cleanup -RelativePath $Path } catch { $rejected = $true }
    if (-not $rejected) { throw "Cleanup accepted invalid target: $Path" }
}

try {
    foreach ($candidate in @('.', '..\AGENTS.md', '*.bin', 'file:stream', 'C:\Windows', '.. \AGENTS.md')) {
        Assert-Rejected $candidate
    }
    $null = New-Item -ItemType Junction -Path "$testRoot/link" -Target "$testRoot/keep"
    Assert-Rejected "$testName/link/marker.txt"
    Assert-Rejected $testName
    if ((Get-Content -LiteralPath "$testRoot/keep/marker.txt") -ne 'preserve') {
        throw 'Cleanup changed a linked target'
    }
    # Remove only the junction itself, without recursion; leave its target intact.
    Remove-Item -LiteralPath "$testRoot/link" -Force
    Set-Content -LiteralPath "$testRoot/discard.txt" -Value 'scratch'
    & $cleanup -RelativePath "$testName/discard.txt" -WhatIf
    if (-not (Test-Path -LiteralPath "$testRoot/discard.txt")) { throw 'WhatIf deleted a file' }
    & $cleanup -RelativePath "$testName/discard.txt"
    if (Test-Path -LiteralPath "$testRoot/discard.txt") { throw 'Cleanup did not delete the named file' }
    if (-not (Test-Path -LiteralPath "$testRoot/keep/marker.txt")) { throw 'Cleanup deleted a sibling' }
    Write-Output 'Scratch cleanup boundary, junction, WhatIf, and deletion checks passed.'
}
finally {
    if (Test-Path -LiteralPath "$testRoot/link") { Remove-Item -LiteralPath "$testRoot/link" -Force }
    & $cleanup -RelativePath $testName
}
