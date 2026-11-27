<#
.SYNOPSIS
    Publishes Meerkat for win-x64 and packages the release archives.

.DESCRIPTION
    Produces two zip files plus a SHA-256 checksum file in the output directory:
      - Meerkat-<version>-win-x64.zip                 requires the .NET Desktop Runtime
      - Meerkat-<version>-win-x64-self-contained.zip  bundles the runtime

.EXAMPLE
    ./build/Publish-Release.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
$project = Join-Path $repositoryRoot 'src\Meerkat.App\Meerkat.App.csproj'
$runtime = 'win-x64'

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$variants = @(
    @{ Suffix = ''; SelfContained = 'false' },
    @{ Suffix = '-self-contained'; SelfContained = 'true' }
)

# Build output goes to the host so that only the archive items end up in $archives.
$archives = foreach ($variant in $variants) {
    $name = "Meerkat-$Version-$runtime$($variant.Suffix)"
    $publishDirectory = Join-Path $OutputDirectory $name

    dotnet publish $project `
        --configuration Release `
        --runtime $runtime `
        --self-contained $variant.SelfContained `
        --output $publishDirectory `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $name."
    }

    Copy-Item (Join-Path $repositoryRoot 'LICENSE') $publishDirectory
    Copy-Item (Join-Path $repositoryRoot 'README.md') $publishDirectory

    $archive = Join-Path $OutputDirectory "$name.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive
    Remove-Item $publishDirectory -Recurse -Force
    Get-Item $archive
}

$checksums = $archives | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
}
Set-Content -Path (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $checksums -Encoding ascii

$archives | ForEach-Object { Write-Host "Created $($_.FullName)" }
