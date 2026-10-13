#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet = & (Join-Path $repoRoot 'scripts\dev\Get-Dotnet.ps1')
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish'))
$output = Join-Path $publishRoot $Runtime
$invocation = [guid]::NewGuid().ToString('N')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$staging = Join-Path $artifactRoot "releases\staging\$Runtime\$invocation"
$build = Join-Path $artifactRoot "releases\build\$Runtime\$invocation"
$previous = Join-Path $artifactRoot "releases\previous\$Runtime\$invocation"
function Assert-PublishPath([string]$Target) {
    $full = [IO.Path]::GetFullPath($Target)
    if (-not $full.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publication target is outside the artifact directory.'
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Publication paths must not contain junctions or symbolic links.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if (Test-Path -LiteralPath $full) {
        if (Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}) {
            throw 'Publication directory contains a junction or symbolic link.'
        }
    }
}
Assert-PublishPath $output
Assert-PublishPath $staging
Assert-PublishPath $build
Assert-PublishPath $previous
New-Item -ItemType Directory -Path $staging -Force | Out-Null
& $dotnet publish (Join-Path $repoRoot 'src\ClawBox.Executor') -c Release -r $Runtime --self-contained true -o $staging --artifacts-path $build --disable-build-servers -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $staging -Runtime $Runtime
$files = @(Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($staging,$_.FullName).Replace('\','/'); bytes=$_.Length;
       sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
@{ runtime=$Runtime; version=$version; files=$files } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $staging 'package-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $staging -Runtime $Runtime -RequireManifest
Assert-PublishPath $output
Assert-PublishPath $staging
if (Test-Path -LiteralPath $output) {
    Assert-PublishPath $previous
    New-Item -ItemType Directory -Path (Split-Path $previous -Parent) -Force | Out-Null
    Move-Item -LiteralPath $output -Destination $previous
}
try { Move-Item -LiteralPath $staging -Destination $output }
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $output)) {
        Assert-PublishPath $previous
        Assert-PublishPath $output
        Move-Item -LiteralPath $previous -Destination $output
    }
    throw
}
Write-Host "Published and verified: $output"
