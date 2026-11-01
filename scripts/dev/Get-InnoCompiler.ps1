#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Portable, unofficial repack of upstream Inno Setup; pin the entire archive.
# No machine-wide installation, registry registration or automatic tool upgrades.
$version = '7.1.0'
$sha256 = 'AAD15C662593C6F5656457A5C001F4591BABD72820CACF63F9706D4AF5D93B75'
$cache = Join-Path $env:LOCALAPPDATA "ClawBox/tools/inno-portable-$version"
$archive = Join-Path $cache "tools.innosetup.$version.zip"
$compiler = Join-Path $cache 'package/tools/ISCC.exe'
$ancestor = $cache
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Compiler cache contains a reparse point.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$version/tools.innosetup.$version.nupkg" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $sha256) { throw 'Inno Setup package checksum mismatch. Remove the cached archive and retry.' }
if (-not (Test-Path -LiteralPath $compiler)) { Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $cache 'package') }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup compiler missing.' }
return $compiler
