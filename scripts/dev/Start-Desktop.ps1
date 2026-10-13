#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet=& (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'artifacts\publish\win-x64\ClawBox.Executor.exe'))) {
    & (Join-Path $repoRoot 'scripts\release\Publish.ps1') -Runtime win-x64
}
& $dotnet run --project (Join-Path $repoRoot 'src\ClawBox.Desktop') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'Quantum Harness exited with an error.' }
