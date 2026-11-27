<#
.SYNOPSIS
    Prints the CHANGELOG.md section of a version, or nothing if the version has no section.

.EXAMPLE
    ./build/Get-ReleaseNotes.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$changelog = Join-Path (Join-Path $PSScriptRoot '..') 'CHANGELOG.md'
$heading = "## [$Version]"
$section = New-Object System.Collections.Generic.List[string]
$isInSection = $false

foreach ($line in Get-Content -LiteralPath $changelog -Encoding UTF8) {
    if ($line.StartsWith('## [')) {
        if ($isInSection) { break }
        $isInSection = $line.StartsWith($heading)
        continue
    }

    if ($isInSection) { $section.Add($line) }
}

($section -join "`n").Trim()
