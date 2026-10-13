#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDataRoot,
    [string]$DestinationDataRoot = (Join-Path $env:LOCALAPPDATA 'ClawBox'),
    [switch]$IncludeSdk
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceDataRoot).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationDataRoot).TrimEnd('\')
if (-not [IO.Path]::IsPathFullyQualified($SourceDataRoot) -or -not [IO.Path]::IsPathFullyQualified($DestinationDataRoot) -or
    $source.StartsWith('\\') -or $destination.StartsWith('\\') -or $source -eq $destination -or
    $source -eq [IO.Path]::GetPathRoot($source).TrimEnd('\') -or $destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\') -or
    $source.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Require distinct, non-nested local data roots.' }
function Assert-NoLinks([string]$Path) {
    for ($directory = [IO.DirectoryInfo]::new($Path); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Data roots must not traverse filesystem links.' }
    }
}
Assert-NoLinks $source
Assert-NoLinks $destination
$pairings = Join-Path $source 'connections'
if (-not (Test-Path -LiteralPath $pairings -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'desktop/gateway.secrets.json') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'executor/tokens.secrets.json') -PathType Leaf)) { throw 'Source must contain complete existing pairing and credential state.' }
# Explicit source only: do not scan unrelated applications or guess a pairing.
$folders = @('connections', 'executor')
if ($IncludeSdk) { $folders += 'sdk' }
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($folder in $folders) {
    $root = Join-Path $source $folder
    if (-not (Test-Path -LiteralPath $root)) { continue }
    Assert-NoLinks $root
    foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source state contains a filesystem link.' }
        if (-not $entry.PSIsContainer -and $entry.Name -ne 'executor.lock') { $files.Add($entry) }
    }
}
$files.Add([IO.FileInfo]::new((Join-Path $source 'desktop/gateway.secrets.json')))
if (Test-Path -LiteralPath (Join-Path $source 'project-runtime.json')) { $files.Add([IO.FileInfo]::new((Join-Path $source 'project-runtime.json'))) }
# Lock the executor before copying SQLite and its journal; never copy live state.
$lease = $null
try {
    $lease = [IO.File]::Open((Join-Path $source 'executor/executor.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $plan = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Join-Path $destination $relative
        Assert-NoLinks (Split-Path $target -Parent)
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        # Existing profiles reference the logical AppData root. Move only known paths
        # within that root; never modify the host pin, tokens or user-selected workspace.
        if ($file.Name -eq 'connection.json' -or $relative -eq 'project-runtime.json') {
            $config = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
            $logical = Join-Path $env:LOCALAPPDATA 'ClawBox'
            $names = if ($file.Name -eq 'connection.json') { @('knownHostsFile', 'identityFile') } else { @('dotnetRoot', 'nodeRoot', 'javaHome', 'mavenHome', 'gradleHome', 'nugetCache', 'mavenCache', 'gradleCache') }
            foreach ($name in $names) {
                $property = $config.PSObject.Properties[$name]
                if ($null -eq $property -or $property.Value -isnot [string]) { continue }
                foreach ($oldRoot in @($logical, $source)) {
                    if ($property.Value.StartsWith($oldRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        $suffix = $property.Value.Substring($oldRoot.Length + 1)
                        $newRoot = if (($suffix -eq 'sdk' -or $suffix.StartsWith('sdk\')) -and -not $IncludeSdk) { $source } else { $destination }
                        $property.Value = Join-Path $newRoot $suffix
                        break
                    }
                }
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes(($config | ConvertTo-Json -Depth 20))
        }
        if (Test-Path -LiteralPath $target) {
            if ([IO.File]::GetAttributes($target) -band [IO.FileAttributes]::ReparsePoint) { throw 'Destination contains a filesystem link.' }
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($target))) -ne
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) { throw "Existing destination differs: $relative. Nothing overwritten; reconcile explicitly." }
            continue
        }
        $plan.Add(@{ Target = $target; Bytes = $bytes })
    }
    & (Join-Path $PSScriptRoot 'Set-PrivateDirectory.ps1') -Path $destination
    $prepared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $plan) {
        $parent = Split-Path $item.Target -Parent
        Assert-NoLinks $parent
        if ($prepared.Add($parent)) { & (Join-Path $PSScriptRoot 'Set-PrivateDirectory.ps1') -Path $parent }
        $stage = $item.Target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $stream = [IO.File]::Open($stage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($item.Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
            [IO.File]::Move($stage, $item.Target, $false)
        } finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage } }
    }
    Write-Host 'Pairing and private executor state restored. Original files, desktop settings and conversation history retained. No requests replayed; no credentials regenerated.'
} finally { if ($null -ne $lease) { $lease.Dispose() } }
