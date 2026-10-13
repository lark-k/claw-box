[CmdletBinding()]
param([string]$DotnetRoot, [string]$NodeRoot, [string]$JavaHome, [string]$MavenHome, [string]$GradleHome)
$ErrorActionPreference = 'Stop'
function RuntimeRoot([string]$Name, [int]$Levels) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { return '' }
    $path = $command.Source
    for ($i = 0; $i -lt $Levels; $i++) { $path = Split-Path $path -Parent }
    $original = $path
    $item = Get-Item -LiteralPath $path
    while ($null -ne $item) {
        if ($item.LinkType) {
            return [IO.Path]::GetFullPath([IO.Path]::Combine($item.ResolveLinkTarget($true).FullName, [IO.Path]::GetRelativePath($item.FullName, $original)))
        }
        $item = $item.Parent
    }
    return $original
}
$root = Join-Path $env:LOCALAPPDATA 'ClawBox'
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (-not $DotnetRoot) {
    $private = Join-Path $root 'sdk'
    $DotnetRoot = if (Test-Path (Join-Path $private 'dotnet.exe')) { $private } else { RuntimeRoot 'dotnet.exe' 1 }
}
if (-not $NodeRoot) { $NodeRoot = RuntimeRoot 'node.exe' 1 }
if (-not $JavaHome) { $JavaHome = RuntimeRoot 'java.exe' 2 }
if (-not $MavenHome) { $MavenHome = RuntimeRoot 'mvn.cmd' 2 }
if (-not $GradleHome) { $GradleHome = RuntimeRoot 'gradle.bat' 2 }
$configuration = [ordered]@{
    dotnetRoot = $DotnetRoot; nodeRoot = $NodeRoot; javaHome = $JavaHome; mavenHome = $MavenHome; gradleHome = $GradleHome
    nugetCache = Join-Path $env:USERPROFILE '.nuget/packages'
    mavenCache = Join-Path $env:USERPROFILE '.m2/repository'
    gradleCache = Join-Path $env:USERPROFILE '.gradle'
}
foreach ($name in @($configuration.Keys)) {
    if ($configuration[$name] -and -not (Test-Path -LiteralPath $configuration[$name] -PathType Container)) { $configuration[$name] = '' }
}
$file = Join-Path $root 'project-runtime.json'
$stage = "$file.$([guid]::NewGuid().ToString('N')).tmp"
$configuration | ConvertTo-Json | Set-Content -LiteralPath $stage -Encoding utf8NoBOM
Move-Item -LiteralPath $stage -Destination $file -Force
Write-Output "Runtime configuration: $file"
$configuration.GetEnumerator() | ForEach-Object { Write-Output "$($_.Key): $($_.Value)" }
