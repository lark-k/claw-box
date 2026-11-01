#requires -Version 7.4
[CmdletBinding()]
param([string]$PackagePath, [string]$IsccPath, [switch]$LegacyWindows)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $PackagePath) {
    & (Join-Path $PSScriptRoot 'Publish-Desktop.ps1') -Runtime win-x64 -SideBySide -LegacyWindows:$LegacyWindows
    $distribution = if ($LegacyWindows) { 'win-x64-legacy' } else { 'win-x64' }
    $PackagePath = Join-Path $repo "artifacts/publish/QuantumHarness/$version/$distribution"
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
& (Join-Path $PSScriptRoot 'Test-DesktopPackage.ps1') -Path $PackagePath -Runtime win-x64 -LegacyWindows:$LegacyWindows
$manifest = Get-Content -LiteralPath (Join-Path $PackagePath 'desktop-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -cne $version) { throw 'Package and repository versions differ.' }
if (-not $IsccPath) { $IsccPath = & (Join-Path $repo 'scripts/dev/Get-InnoCompiler.ps1') }
$output = Join-Path $repo 'artifacts/releases/installers'
$ancestor = $output
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installer publication path contains a reparse point.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$legacyDefine = if ($LegacyWindows) { '/DLegacyWindows=1' } else { '/DLegacyWindows=0' }
& $IsccPath '/Qp' "/DPackageDir=$PackagePath" "/DProductVersion=$version" "/DInstallerOutput=$stage" $legacyDefine (Join-Path $repo 'src/ClawBox.Installer/QuantumHarness.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$suffix = if ($LegacyWindows) { '-legacy' } else { '' }
$filename = "QuantumHarness-$version-win-x64$suffix-Setup.exe"
$installer = Join-Path $output $filename
Move-Item -LiteralPath (Join-Path $stage $filename) -Destination $installer -Force
@{ product='Quantum Harness'; version=$version; runtime='win-x64'; windowsCompatibility=$(if ($LegacyWindows) { 'legacy' } else { 'standard' }); file=$filename; sha256=(Get-FileHash -LiteralPath $installer).Hash; signed=((Get-AuthenticodeSignature -LiteralPath $installer).Status -eq 'Valid') } |
    ConvertTo-Json | Set-Content -LiteralPath ($installer + '.sha256.json') -Encoding utf8
Write-Host "Installer ready: $installer"
Write-Host 'Unsigned local build: production distribution requires a trusted code-signing certificate.'
