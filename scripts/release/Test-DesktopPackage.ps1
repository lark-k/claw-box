#requires -Version 7.4
param([Parameter(Mandatory)][string]$Path,[ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
$Path=[IO.Path]::GetFullPath($Path)
foreach ($required in @('QuantumHarness.exe','QuantumHarness.dll','QuantumHarness.runtimeconfig.json','hostfxr.dll','coreclr.dll','PresentationFramework.dll','executor/ClawBox.Executor.exe','desktop-manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Path $required) -PathType Leaf)) { throw "Package missing $required" }
}
$files=@(Get-ChildItem -LiteralPath $Path -File -Recurse)
if ($files | Where-Object { $_.Name -match '(?i)\.secrets\.json$|^(connection|settings|conversations)\.json$|^appsettings\..+\.json$|\.(sqlite|db|docx|log|pem|key|pfx)$|^known_hosts$|^tunnel_ed25519|^\.env($|\.)' }) { throw 'Private/runtime data found in application package.' }
$manifest=Get-Content -LiteralPath (Join-Path $Path 'desktop-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.runtime -cne $Runtime -or $manifest.product -cne 'Quantum Harness') { throw 'Wrong package identity.' }
if ($files.Count -ne $manifest.files.Count+1) { throw 'Unexpected files in package.' }
$seen=@{}
foreach ($file in $manifest.files) {
    $full=[IO.Path]::GetFullPath((Join-Path $Path $file.path))
    if (-not $full.StartsWith($Path+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($full)) { throw 'Invalid or duplicate manifest path.' }
    $seen[$full]=$true
    if ((Get-Item -LiteralPath $full).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $file.sha256) { throw 'Package checksum mismatch.' }
}
$bytes=[IO.File]::ReadAllBytes((Join-Path $Path 'QuantumHarness.exe'))
if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw 'Invalid desktop apphost.' }
$offset=[BitConverter]::ToInt32($bytes,0x3c)
if ($offset -lt 64 -or $offset -gt $bytes.Length-24 -or [BitConverter]::ToUInt32($bytes,$offset) -ne 0x00004550) { throw 'Invalid desktop PE header.' }
$expected=if ($Runtime -eq 'win-x64') { 0x8664 } else { 0xaa64 }
if ([BitConverter]::ToUInt16($bytes,$offset+4) -ne $expected) { throw 'Desktop apphost architecture mismatch.' }
Write-Host "Desktop package verified: $Path"
