[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sdkVersion = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$sdkDirectory = Join-Path $env:LOCALAPPDATA 'ClawBox\sdk'
New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
$installer = Join-Path $sdkDirectory 'dotnet-install.ps1'
Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version $sdkVersion -InstallDir $sdkDirectory -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
& $dotnet --version
