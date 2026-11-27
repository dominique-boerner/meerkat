<#
.SYNOPSIS
    Creates a WSL instance with native Docker for Meerkat, provisioned through cloud-init.

.EXAMPLE
    .\New-MeerkatWsl.ps1
    .\New-MeerkatWsl.ps1 -Name MeerkatTest -Image Ubuntu-24.04
#>
[CmdletBinding()]
param(
    [string]$Name = 'Meerkat',
    [string]$Image = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'

# wsl.exe prints UTF-16 by default, which breaks string comparisons on its output.
$env:WSL_UTF8 = '1'

function Invoke-Wsl {
    wsl.exe @args
    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$existing = (wsl.exe --list --quiet) | ForEach-Object { $_.Trim() }
if ($existing -contains $Name) {
    throw "A WSL distribution named '$Name' already exists. Remove it with 'wsl --unregister $Name' or choose another -Name."
}

# WSL's cloud-init data source looks up user data by instance name in this folder.
$cloudInitDirectory = Join-Path $env:USERPROFILE '.cloud-init'
New-Item -ItemType Directory -Force -Path $cloudInitDirectory | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'meerkat.user-data') -Destination (Join-Path $cloudInitDirectory "$Name.user-data") -Force

Write-Host "Installing $Image as '$Name'..."
Invoke-Wsl --install $Image --name $Name --no-launch

Write-Host 'Waiting for cloud-init (installs Docker and starts the demo containers)...'
Invoke-Wsl -d $Name -u root -- cloud-init status --wait --long

# wsl.conf (default user) is only read when the instance boots.
Invoke-Wsl --terminate $Name

Write-Host 'Containers:'
Invoke-Wsl -d $Name --exec docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}'

Write-Host "Done. Set the WSL distribution in the Meerkat settings to '$Name'."
