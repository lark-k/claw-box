#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path $repo ('artifacts/startup-report-test/' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $root 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$exe = Join-Path $payload 'QuantumHarness.exe'
# The fixture is data, never launched or registered as an installed application.
'fixture' | Set-Content -LiteralPath $exe -Encoding utf8
$manifest = Join-Path $payload 'desktop-manifest.json'
$entry = @{path='QuantumHarness.exe'; bytes=(Get-Item -LiteralPath $exe).Length; sha256=(Get-FileHash -LiteralPath $exe).Hash}
@{ version='0.0.1'; runtime='win-x64'; files=@($entry) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest
$script = Join-Path $repo 'scripts/dev/Get-DesktopStartupReport.ps1'
function Collect([string]$Name) {
    $destination = Join-Path $root $Name
    & $script -ExePath $exe -OutputDirectory $destination | Out-Host
    return (Get-Content -LiteralPath (Join-Path $destination 'startup-report.json') -Raw | ConvertFrom-Json)
}
$valid = Collect 'valid'
if ($valid.package.checkedFiles -ne 1 -or $valid.package.problems.Count -ne 0 -or $null -ne $valid.launch) { throw 'Valid payload must pass without launching an EXE.' }
'tampered' | Set-Content -LiteralPath $exe -Encoding utf8
$damaged = Collect 'damaged'
if ($damaged.package.problems[0].status -ne 'checksum-mismatch') { throw 'Corrupt payload must be identified.' }
$entry.path = 'missing.dll'
@{ version='0.0.1'; runtime='win-x64'; files=@($entry) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest
$missing = Collect 'missing'
if ($missing.package.problems[0].status -ne 'missing') { throw 'Missing payload must be identified.' }
$entry.path = '../outside.dll'
@{ version='0.0.1'; runtime='win-x64'; files=@($entry) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest
$invalid = Collect 'invalid-path'
if ($invalid.package.error -ne 'Invalid package manifest path.') { throw 'Manifest paths must stay inside package.' }
Write-Host "Startup diagnostics passed without application launch or installation: $root"
