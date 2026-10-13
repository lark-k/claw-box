#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $repoRoot 'src\ClawBox.Executor'
$fixtures = @((Join-Path $project 'appsettings.Local.json'), (Join-Path $project 'publish-test.secrets.json'))
foreach ($fixture in $fixtures) {
    if (Test-Path -LiteralPath $fixture) { throw 'Refusing to replace an existing developer configuration for the publish test.' }
}
$created = @()
$process = $null
$testRoot = Join-Path $env:LOCALAPPDATA ('ClawBox\tests\' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($fixture in $fixtures) {
        '{"publishExclusionSentinel":true}' | Set-Content -LiteralPath $fixture -Encoding utf8
        $created += $fixture
    }
    & (Join-Path $repoRoot 'scripts\release\Publish.ps1') -Runtime $Runtime
    if ($Runtime -ne 'win-x64' -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        Write-Host 'Package structure verified; runtime smoke test requires an x64 Windows host.'
        return
    }
    $workspace = Join-Path $testRoot 'workspace'
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    $tokens = @{ adminToken=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32));
                 workerToken=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
    $tokenPath = Join-Path $testRoot 'tokens.secrets.json'
    $tokens | ConvertTo-Json | Set-Content -LiteralPath $tokenPath -Encoding utf8
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $package = Join-Path $repoRoot 'artifacts\publish\win-x64'
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'ClawBox.Executor.exe'))
    $info.WorkingDirectory = $package
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in @($info.Environment.Keys | Where-Object {$_ -like 'Executor__*'})) { $info.Environment.Remove($name) | Out-Null }
    $info.Environment['Executor__AdminToken'] = $tokens.adminToken
    $info.Environment['Executor__WorkerToken'] = $tokens.workerToken
    $info.Environment['Executor__AllowedRoots__0'] = $workspace
    $info.Environment['Executor__DataDirectory'] = Join-Path $testRoot 'state'
    $info.Environment['Executor__Port'] = "$port"
    $info.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw 'Published executor exited before becoming ready.' }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$port/healthz" -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready=$true; break }
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Published executor did not become ready.' }
    & (Join-Path $repoRoot 'scripts\dev\Invoke-Demo.ps1') -Port $port -Workspace $workspace -TokensFile $tokenPath
    Write-Host 'Published EXE smoke test and private configuration exclusion passed.'
} finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($fixture in $created) { Remove-Item -LiteralPath $fixture -Force }
    # Only this invocation's GUID directory may be removed.
    $parent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ClawBox\tests'))
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
