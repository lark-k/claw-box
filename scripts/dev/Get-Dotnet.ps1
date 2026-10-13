$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sdkVersion = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$privateSdk = Join-Path $env:LOCALAPPDATA 'ClawBox\sdk\dotnet.exe'
if (Test-Path -LiteralPath $privateSdk) { return $privateSdk }
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($systemDotnet -and ((& $systemDotnet.Source --list-sdks) -match [regex]::Escape($sdkVersion))) {
    return $systemDotnet.Source
}
throw "Missing .NET SDK $sdkVersion. Run scripts/dev/Setup-Dev.ps1."
