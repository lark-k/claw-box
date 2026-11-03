[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$file = (Resolve-Path -LiteralPath $Installer).Path
$root = Join-Path $repo ('artifacts/installer-smoke/' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'app'
New-Item -ItemType Directory -Force $root | Out-Null
$process = $null
$shortcuts = @{}
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    if ($folder) {
        $link = Join-Path $folder 'Quantum Harness.lnk'
        if (Test-Path -LiteralPath $link -PathType Leaf) { $shortcuts[$link] = [IO.File]::ReadAllBytes($link) }
    }
}
try {
    $setup = Start-Process -FilePath $file -ArgumentList @('/S', "/D=$install") -WindowStyle Hidden -PassThru
    if (-not $setup.WaitForExit(180000)) { $setup.Kill($true); throw 'Installer did not finish within three minutes.' }
    if ($setup.ExitCode -ne 0) { throw "Installer failed: $($setup.ExitCode)" }
    $exe = Join-Path $install 'Quantum Harness.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Installed Quantum Harness executable is missing.' }
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductName -ne 'Quantum Harness') { throw 'Installed product name is incorrect.' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.Environment['DSH_HOME'] = Join-Path $root 'harness'
    $start.Environment.Remove('QUANTUM_GATEWAY_TOKEN') | Out-Null
    $start.Environment.Remove('DEEPSEEK_API_KEY') | Out-Null
    $start.ArgumentList.Add('--user-data-dir=' + (Join-Path $root 'electron'))
    $start.ArgumentList.Add('--remote-debugging-port=9223')
    $process = [Diagnostics.Process]::Start($start)
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        try { $null = Invoke-RestMethod 'http://127.0.0.1:9223/json/list' -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Installed application did not expose its test renderer.' }
    & node (Join-Path $repo 'scripts/dev/Inspect-HarnessBox.mjs') 9223
    if ($LASTEXITCODE -ne 0) { throw 'Installed application UI inspection failed.' }
    Write-Output "Installed application startup and Quantum box UI passed: $install"
    $process.Kill($true)
    $process.WaitForExit()
    $retained = @{}
    foreach ($relative in @('harness/quantum-retention.txt', 'electron/quantum-retention.txt', 'workspace/quantum-retention.txt')) {
        $marker = Join-Path $root $relative
        New-Item -ItemType Directory -Force (Split-Path $marker -Parent) | Out-Null
        [IO.File]::WriteAllText($marker, 'Quantum installer retention fixture')
        $retained[$marker] = (Get-FileHash -LiteralPath $marker).Hash
    }
    $profilePatch = Join-Path $root 'harness/profiles/desktop/cordis.patch.yml'
    if (Test-Path -LiteralPath $profilePatch) { $retained[$profilePatch] = (Get-FileHash -LiteralPath $profilePatch).Hash }
    # Reinstall checks replacement and retention; cross-version migration needs its own version-pair qualification.
    $reinstall = Start-Process -FilePath $file -ArgumentList @('/S') -WindowStyle Hidden -PassThru
    if (-not $reinstall.WaitForExit(180000)) { $reinstall.Kill($true); throw 'Replacement installation timed out.' }
    if ($reinstall.ExitCode -ne 0) { throw "Same-version replacement installation failed: $($reinstall.ExitCode)" }
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Replacement installation did not retain its registered installation directory.' }
    foreach ($marker in $retained.Keys) {
        if (-not (Test-Path -LiteralPath $marker) -or (Get-FileHash -LiteralPath $marker).Hash -ne $retained[$marker]) {
            throw 'Replacement installation changed profile or workspace retention fixtures.'
        }
    }
    Write-Output 'Replacement installation passed; profile, Electron settings and workspace fixtures retained.'
} finally {
  try {
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $uninstaller = Join-Path $install 'Uninstall Quantum Harness.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $remove = Start-Process -FilePath $uninstaller -ArgumentList @('/S', "_?=$install") -WindowStyle Hidden -PassThru
        if (-not $remove.WaitForExit(90000) -or $remove.ExitCode -ne 0) { throw 'Test installation could not be uninstalled.' }
        if (Test-Path -LiteralPath (Join-Path $install 'Quantum Harness.exe')) { throw 'Test uninstaller left the application executable.' }
        Write-Output 'Isolated test installation uninstalled successfully; user profiles were retained.'
    }
  } finally {
    foreach ($link in $shortcuts.Keys) { [IO.File]::WriteAllBytes($link, $shortcuts[$link]) }
  }
}
