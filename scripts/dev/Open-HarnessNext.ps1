[CmdletBinding()]
param(
    [string]$Config = (Join-Path $env:LOCALAPPDATA 'QuantumHarnessNext/model.secrets.json'),
    [switch]$CheckOnly,
    [switch]$Source
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$state = Join-Path $env:LOCALAPPDATA 'QuantumHarnessNext'
$gateway = $null
$gatewayStarted = $false
$mutex = [Threading.Mutex]::new($false, 'Local\QuantumHarnessNextPreview')
$owned = $false
$buildLock = [Threading.Mutex]::new($false, 'Local\QuantumHarnessNextBuild')
$buildOwned = $false
$oldToken = $env:QUANTUM_GATEWAY_TOKEN
try {
    if (-not $CheckOnly) {
        try { $buildOwned = $buildLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $buildOwned = $true }
        if (-not $buildOwned) { Write-Output 'Build in progress. Start the preview after packaging completes.'; return }
        try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { Write-Output 'The preview is already running; open its taskbar or tray window.'; return }
    }
    if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) { throw "Private model configuration is missing: $Config" }
    $settings = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
    if (-not $settings.model -or -not $settings.token) { throw 'Private configuration needs model and gateway token fields.' }
    $python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $app = Join-Path $repo 'src/Quantum.Harness.Upstream/apps/desktop'
    $packaged = Join-Path $app '.desktop-build/targets/win-x64/unsigned-artifacts/win-unpacked/Quantum Harness.exe'
    $shortApplication = Join-Path $state 'app/Quantum Harness.exe'
    if (Test-Path -LiteralPath $shortApplication) { $packaged = $shortApplication }
    $receipt = Join-Path $repo 'src/Quantum.Harness.Upstream/.dsh-build/quantum-brand.json'
    $stamp = "$packaged.quantum-brand.sha256"
    $currentPackage = (Test-Path -LiteralPath $receipt) -and (Test-Path -LiteralPath $stamp) -and
        ((Get-Content -Raw -LiteralPath $stamp).Trim() -eq (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash)
    # Never reopen an older frozen app after the product overlay changed.
    if ($Source -or -not $currentPackage) { $packaged = Join-Path $state 'source-preview-not-packaged.exe' }
    $required = if (Test-Path -LiteralPath $packaged) { @() } else { @('lib/main.js','node_modules/electron/dist/electron.exe','.desktop-build/targets/win-x64/runtime/primary-runtime/runtime.json') }
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $relative))) { throw "Desktop preparation is incomplete: $relative. See docs/development/harness-next.md." }
    }
    if ($CheckOnly) { Write-Output 'Preview prerequisites and private model configuration are available.'; return }
    New-Item -ItemType Directory -Force $state | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($python)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-B', (Join-Path $repo 'src/Quantum.ModelGateway/gateway.py'), '--config', (Resolve-Path -LiteralPath $Config).Path, '--port', '0')) {
        $start.ArgumentList.Add($argument)
    }
    $gateway = [Diagnostics.Process]::new()
    $gateway.StartInfo = $start
    if (-not $gateway.Start()) { throw 'Could not start the local test model gateway.' }
    $gatewayStarted = $true
    $ready = $gateway.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(15000) -or $ready.Result -notmatch 'listening on 127\.0\.0\.1:(\d+)$') {
        throw 'The local test model gateway failed to become ready. Check private model configuration.'
    }
    $port = [int]$Matches[1]
    $env:QUANTUM_GATEWAY_TOKEN = $settings.token
    # The owned gateway uses a fresh port. Refresh only this provider's URL in the preview profile.
    $patch = Join-Path $state 'harness/profiles/desktop/cordis.patch.yml'
    if (Test-Path -LiteralPath $patch) {
        & node (Join-Path $PSScriptRoot 'Update-HarnessProfile.mjs') $patch $port
        if ($LASTEXITCODE -ne 0) { throw 'Could not update the upstream YAML model profile. Existing settings were retained.' }
    }
    if (Test-Path -LiteralPath $packaged) {
        $env:DSH_HOME = Join-Path $state 'harness'
        $instance = Start-Process -FilePath $packaged -ArgumentList @('--user-data-dir="' + (Join-Path $state 'electron') + '"', '--remote-debugging-port=9222') -PassThru
        $instance.WaitForExit()
        if ($instance.ExitCode -ne 0) { throw 'Quantum Harness exited unexpectedly. See its private application logs.' }
    } else {
        & (Join-Path $PSScriptRoot 'Start-HarnessNext.ps1') -Surface Desktop -Model $settings.model -GatewayPort $port -DisableThinking *> (Join-Path $state 'preview.log')
    }
} catch {
    if ($CheckOnly) { throw }
    New-Item -ItemType Directory -Force $state | Out-Null
    $message = $_.Exception.Message
    $message | Set-Content -LiteralPath (Join-Path $state 'preview-error.log') -Encoding utf8
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show($message, 'Quantum Harness') | Out-Null
} finally {
    if ($gateway) {
        if ($gatewayStarted -and -not $gateway.HasExited) { $gateway.Kill($true); $gateway.WaitForExit() }
        $gateway.Dispose()
    }
    $env:QUANTUM_GATEWAY_TOKEN = $oldToken
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($buildOwned) { $buildLock.ReleaseMutex() }
    $buildLock.Dispose()
}
