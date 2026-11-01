#requires -Version 7.4
[CmdletBinding()]
param([string]$InstallerPath, [string]$IsccPath, [switch]$LegacyWindows)
$ErrorActionPreference = 'Stop'
$suffix = if ($LegacyWindows) { '-legacy' } else { '' }
$legacyDefine = if ($LegacyWindows) { '/DLegacyWindows=1' } else { '/DLegacyWindows=0' }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $IsccPath) { $IsccPath = & (Join-Path $repo 'scripts/dev/Get-InnoCompiler.ps1') }
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $repo "artifacts/installer-qa/$id"
$appId = "QuantumHarness-Test-$id"
$product = "Quantum Harness Test $id"
$reg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$installDir = Join-Path $root '自定义路径 with spaces/Quantum Harness'
$shortcutDir = Join-Path ([Environment]::GetFolderPath('Programs')) $product
New-Item -ItemType Directory -Path $root -Force | Out-Null
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Setup([string]$Exe, [string[]]$Extra, [bool]$Success = $true) {
    $log = Join-Path $root ([guid]::NewGuid().ToString('N') + '.log')
    $process = Start-Process -FilePath $Exe -ArgumentList (@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="'+$log+'"')) + $Extra) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Installer timed out; inspect $log" }
    if ($Success) { Require ($process.ExitCode -eq 0) "Installer failed ($($process.ExitCode)); inspect $log" }
    else { Require ($process.ExitCode -ne 0) 'Running app, concurrent maintenance or downgrade should be rejected.' }
    if ($Success) {
        # Uninstall's original EXE can exit before its temporary worker finishes.
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $handle = $null
            $busy = [Threading.Mutex]::TryOpenExisting(('Local\QuantumHarness-Maintenance-' + [Environment]::UserName), [ref]$handle)
            if ($handle) { $handle.Dispose() }
            if (-not $busy) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Require (-not $busy) 'Maintenance worker did not finish.'
    }
}
function Build-Fixture([string]$Version) {
    $payload = Join-Path $root "payload-$Version"
    New-Item -ItemType Directory -Path $payload | Out-Null
    $Version | Set-Content -LiteralPath (Join-Path $payload 'QuantumHarness.exe')
    if ($Version -eq '0.0.1') { 'old-component' | Set-Content -LiteralPath (Join-Path $payload 'obsolete.dll') }
    & $IsccPath '/Qp' "/DPackageDir=$payload" "/DProductVersion=$Version" "/DInstallerOutput=$root" "/DInstallerId=$appId" "/DProductName=$product" $legacyDefine (Join-Path $repo 'src/ClawBox.Installer/QuantumHarness.iss') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    return (Join-Path $root "QuantumHarness-$Version-win-x64$suffix-Setup.exe")
}
function Check-Shortcut([string]$Version) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut((Join-Path $shortcutDir "$product.lnk"))
        try { Require ($shortcut.TargetPath -eq (Join-Path $installDir "versions/$Version$suffix/QuantumHarness.exe")) 'Start Menu must point to the complete new version.' }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
$uninstall = Join-Path $installDir 'unins000.exe'
try {
    $old = Build-Fixture '0.0.1'
    $new = Build-Fixture '0.0.2'
    $installArgs = @('/DIR="'+$installDir+'"')
    $running = [Threading.Mutex]::new($false, ('Local\QuantumHarness-' + [Environment]::UserName))
    try { Run-Setup $old $installArgs $false } finally { $running.Dispose() }
    Require (-not (Test-Path -LiteralPath $reg)) 'Blocked installation must not register a product.'
    Run-Setup $old $installArgs
    Require ((Get-ItemProperty -LiteralPath $reg).DisplayVersion -eq '0.0.1') 'First installation must be registered.'
    Check-Shortcut '0.0.1'
    $relocated = Join-Path $root 'another-location'
    Run-Setup $new @('/DIR="'+$relocated+'"') $false
    Require (-not (Test-Path -LiteralPath $relocated)) 'Upgrade must not split registration and uninstall records across locations.'
    Require ((Get-ItemProperty -LiteralPath $reg).DisplayVersion -eq '0.0.1') 'Rejected relocation must preserve original registration.'
    $document = Join-Path $installDir 'user-document.txt'
    $nested = Join-Path $installDir "versions/0.0.1$suffix/user-workspace"
    New-Item -ItemType Directory -Path $nested | Out-Null
    'keep-root-document' | Set-Content -LiteralPath $document
    'keep-nested-document' | Set-Content -LiteralPath (Join-Path $nested 'report.txt')
    Run-Setup $new @()
    Require ((Get-ItemProperty -LiteralPath $reg).DisplayVersion -eq '0.0.2') 'Upgrade must update one product registration.'
    Require (-not (Test-Path (Join-Path $installDir "versions/0.0.2$suffix/obsolete.dll"))) 'New version must not inherit obsolete components.'
    Check-Shortcut '0.0.2'
    Run-Setup $old @() $false
    Require ((Get-ItemProperty -LiteralPath $reg).DisplayVersion -eq '0.0.2') 'Rejected downgrade must preserve current registration.'
    Run-Setup $new @()
    $running = [Threading.Mutex]::new($false, ('Local\QuantumHarness-' + [Environment]::UserName))
    try { Run-Setup $new @() $false; Run-Setup $uninstall @() $false } finally { $running.Dispose() }
    Require (Test-Path -LiteralPath $uninstall) 'Blocked uninstall must leave application intact.'
    $maintenance = [Threading.Mutex]::new($false, ('Local\QuantumHarness-Maintenance-' + [Environment]::UserName))
    try { Run-Setup $new @() $false; Run-Setup $uninstall @() $false } finally { $maintenance.Dispose() }
    Run-Setup $uninstall @()
    Require (-not (Test-Path -LiteralPath $reg)) 'Uninstall must remove registration.'
    Require (-not (Test-Path -LiteralPath $shortcutDir)) 'Uninstall must remove Start Menu entries.'
    foreach ($v in @('0.0.1','0.0.2')) { Require (-not (Test-Path (Join-Path $installDir "versions/$v$suffix/QuantumHarness.exe"))) 'Uninstall must remove all tracked versions.' }
    Require ((Get-Content -LiteralPath $document).Trim() -eq 'keep-root-document') 'User-created files must survive uninstall.'
    Require ((Get-Content -LiteralPath (Join-Path $nested 'report.txt')).Trim() -eq 'keep-nested-document') 'Nested user workspace must survive uninstall.'
    Run-Setup $new $installArgs
    Check-Shortcut '0.0.2'
    Run-Setup $uninstall @()
    Write-Host "Installer lifecycle passed: $root"
} finally {
    if ((Test-Path -LiteralPath $reg) -and (Test-Path -LiteralPath $uninstall)) { Run-Setup $uninstall @() }
}

if ($InstallerPath) {
    $productionKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QuantumHarness_is1'
    Require (-not (Test-Path -LiteralPath $productionKey)) 'Existing user installation found; production smoke cannot replace it.'
    $productionDir = Join-Path $root 'production'
    $version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $productionUninstall = Join-Path $productionDir 'unins000.exe'
    $state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClawBox'
    $snapshot = @{}
    foreach ($folder in @('desktop','connections','executor','workspace')) {
        $path = Join-Path $state $folder
        if (Test-Path -LiteralPath $path) {
            foreach ($file in (Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction Stop)) { $snapshot[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
        }
    }
    try {
        Run-Setup $InstallerPath @('/DIR="'+$productionDir+'"')
        $exe = Join-Path $productionDir "versions/$version$suffix/QuantumHarness.exe"
        Require (Test-Path -LiteralPath $exe) 'Production installer must include desktop EXE.'
        Require (Test-Path (Join-Path $productionDir "versions/$version$suffix/executor/ClawBox.Executor.exe")) 'Production installer must include executor.'
        $visual = Join-Path $root 'installed-visual'
        $process = Start-Process -FilePath $exe -ArgumentList @('--visual-smoke',('"'+$visual+'"')) -WindowStyle Hidden -PassThru
        Require ($process.WaitForExit(60000)) 'Installed application render timed out.'
        Require ($process.ExitCode -eq 0 -and (Test-Path (Join-Path $visual 'chat.png'))) 'Installed self-contained EXE must render successfully.'
        Run-Setup $productionUninstall @()
        foreach ($file in $snapshot.Keys) { Require ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file).Hash -eq $snapshot[$file]) 'Install/uninstall must preserve existing data byte-for-byte.' }
        Require (-not (Test-Path -LiteralPath $productionKey)) 'Production smoke must remove its product registration.'
        Write-Host "Production installer, installed EXE rendering and local data preservation passed: $root"
    } finally {
        if (Test-Path -LiteralPath $productionUninstall) { Run-Setup $productionUninstall @() }
    }
}
