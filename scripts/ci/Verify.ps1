[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet = & (Join-Path $repoRoot 'scripts\dev\Get-Dotnet.ps1')
Push-Location $repoRoot
try {
    & $dotnet restore ClawBox.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $dotnet build ClawBox.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet test ClawBox.sln -c Release --no-build --logger 'trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $dotnet format ClawBox.sln whitespace --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting failed.' }
} finally { Pop-Location }
