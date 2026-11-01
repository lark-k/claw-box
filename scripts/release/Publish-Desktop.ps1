#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$SideBySide, [switch]$LegacyWindows)
$ErrorActionPreference = 'Stop'
if ($LegacyWindows -and $Runtime -ne 'win-x64') { throw 'Legacy Windows compatibility is verified only for win-x64.' }
$distribution = if ($LegacyWindows) { "$Runtime-legacy" } else { $Runtime }
$cetCompat = if ($LegacyWindows) { 'false' } else { 'true' }
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet = & (Join-Path $repoRoot 'scripts/dev/Get-Dotnet.ps1')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$id = [guid]::NewGuid().ToString('N')
$stage = Join-Path $artifactRoot "releases/desktop-stage/$distribution/$id"
$build = Join-Path $artifactRoot "releases/desktop-build/$distribution/$id"
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = if ($SideBySide) { Join-Path $artifactRoot "publish/QuantumHarness/$version/$distribution" } else { Join-Path $artifactRoot "publish/QuantumHarness/$distribution" }
$previous = Join-Path $artifactRoot "releases/desktop-previous/$distribution/$id"
function Assert-ArtifactPath([string]$Target) {
    $full = [IO.Path]::GetFullPath($Target)
    if (-not $full.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Target outside artifacts.' }
    $current = $full
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse point in publication path.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if ((Test-Path -LiteralPath $full) -and (Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) { throw 'Reparse point in publication files.' }
}
foreach ($path in @($stage,$build,$output,$previous)) { Assert-ArtifactPath $path }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& $dotnet publish (Join-Path $repoRoot 'src/ClawBox.Desktop') -c Release -r $Runtime --self-contained true -o $stage --artifacts-path $build --disable-build-servers -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true "-p:CETCompat=$cetCompat"
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
& $dotnet publish (Join-Path $repoRoot 'src/ClawBox.Executor') -c Release -r $Runtime --self-contained true -o (Join-Path $stage 'executor') --artifacts-path $build --disable-build-servers -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true "-p:CETCompat=$cetCompat"
if ($LASTEXITCODE -ne 0) { throw 'Bundled executor publish failed.' }
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path (Join-Path $stage 'executor') -Runtime $Runtime
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
@{product='Quantum Harness';version=$version;runtime=$Runtime;windowsCompatibility=$(if ($LegacyWindows) { 'legacy' } else { 'standard' });files=@(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'desktop-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-DesktopPackage.ps1') -Path $stage -Runtime $Runtime -LegacyWindows:$LegacyWindows
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
if (Test-Path -LiteralPath $output) {
    New-Item -ItemType Directory -Path (Split-Path $previous -Parent) -Force | Out-Null
    Move-Item -LiteralPath $output -Destination $previous
}
try { Move-Item -LiteralPath $stage -Destination $output }
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $output)) { Move-Item -LiteralPath $previous -Destination $output }
    throw
}
$zip = Join-Path $artifactRoot "releases/QuantumHarness-$version-$distribution.zip"
Assert-ArtifactPath $zip
Compress-Archive -LiteralPath $output -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object Hash | ConvertTo-Json | Set-Content -LiteralPath ($zip+'.sha256.json')
Write-Host "Quantum Harness ready: $output"
Write-Host "Portable archive: $zip"
