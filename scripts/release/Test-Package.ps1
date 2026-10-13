#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$RequireManifest
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Path).Path
foreach ($name in @('ClawBox.Executor.exe','ClawBox.Executor.dll','ClawBox.Contracts.dll','coreclr.dll','hostpolicy.dll','hostfxr.dll')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $name))
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw "Invalid Windows binary: $name" }
    $offset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($offset -lt 64 -or $offset -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x00004550) {
        throw "Invalid PE header: $name"
    }
    if ($name -eq 'ClawBox.Executor.exe') {
        $machine = [BitConverter]::ToUInt16($bytes, $offset + 4)
        $expected = if ($Runtime -eq 'win-x64') { 0x8664 } else { 0xaa64 }
        if ($machine -ne $expected) { throw 'Apphost architecture does not match the requested runtime.' }
    }
}
$runtimeConfig = Get-Content (Join-Path $root 'ClawBox.Executor.runtimeconfig.json') -Raw | ConvertFrom-Json
$dependencies = Get-Content (Join-Path $root 'ClawBox.Executor.deps.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.tfm -ne 'net10.0' -or $dependencies.runtimeTarget.name -notlike "*/$Runtime") {
    throw 'Runtime configuration or dependency graph is invalid.'
}
if ('Microsoft.NETCore.App' -notin @($runtimeConfig.runtimeOptions.includedFrameworks.name)) { throw 'Self-contained runtime metadata is missing.' }
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    if ($file.Name -like 'appsettings.*.json' -or $file.Name -like '*.secrets.json' -or
        $file.Name -eq '.env' -or $file.Name -like '.env.*' -or
        $file.Extension -in @('.key','.pem','.pfx','.sqlite','.db','.log','.docx')) {
        throw "Private configuration or user data is present in the package: $($file.Name)"
    }
}
if ($RequireManifest) {
    $manifest = Get-Content (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.runtime -ne $Runtime) { throw 'Manifest runtime mismatch.' }
    $actual = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object Name -ne 'package-manifest.json')
    if ($actual.Count -ne $manifest.files.Count) { throw 'Manifest file count mismatch.' }
    $expectedPaths = @{}
    foreach ($entry in $manifest.files) {
        $full = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $expectedPaths.ContainsKey($full)) {
            throw 'Invalid or duplicate manifest path.'
        }
        $expectedPaths[$full] = $true
        $file = Get-Item -LiteralPath $full
        if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Package integrity check failed: $($entry.path)"
        }
    }
}
Write-Host "Package verification passed: $Runtime"
