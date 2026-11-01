#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipPublish, [switch]$SideBySide, [switch]$LegacyWindows)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $SkipPublish) { & (Join-Path $repoRoot 'scripts/release/Publish-Desktop.ps1') -Runtime win-x64 -SideBySide:$SideBySide -LegacyWindows:$LegacyWindows }
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$distribution = if ($LegacyWindows) { 'win-x64-legacy' } else { 'win-x64' }
$package=if ($SideBySide) { Join-Path $repoRoot "artifacts/publish/QuantumHarness/$version/$distribution" } else { Join-Path $repoRoot "artifacts/publish/QuantumHarness/$distribution" }
& (Join-Path $repoRoot 'scripts/release/Test-DesktopPackage.ps1') -Path $package -Runtime win-x64 -LegacyWindows:$LegacyWindows
$destination=Join-Path $repoRoot ('artifacts/desktop-qa/publish-'+[guid]::NewGuid().ToString('N'))
$startupDestination = Join-Path $destination 'fresh-startup'
$startupProcess = Start-Process -FilePath (Join-Path $package 'QuantumHarness.exe') -ArgumentList @('--startup-smoke',('"'+$startupDestination+'"')) -WindowStyle Hidden -PassThru
if (-not $startupProcess.WaitForExit(30000)) { $startupProcess.Kill(); throw 'Fresh startup timed out.' }
if ($startupProcess.ExitCode -ne 0 -or -not (Test-Path (Join-Path $startupDestination 'startup.json'))) { throw 'Fresh home screen did not start.' }
$startup = Get-Content -LiteralPath (Join-Path $startupDestination 'startup.json') -Raw | ConvertFrom-Json
if ($startup.mainWindow -ne $true -or $startup.connectionChooser -ne $false -or $startup.connected -ne $false) { throw 'Fresh startup must remain on the home screen without connection dialogs.' }
$process=Start-Process -FilePath (Join-Path $package 'QuantumHarness.exe') -ArgumentList @('--visual-smoke',('"'+$destination+'"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop visual smoke timed out.' }
if ($process.ExitCode -ne 0) { throw 'Published desktop EXE failed.' }
Add-Type -AssemblyName System.Drawing
foreach ($name in @('chat.png','approval.png','compact.png','thinking-start.png','thinking-scan.png','thinking-progress.png','thinking-complete.png','thinking-review.png','bubble-wrap.png','inline-progress-expanded.png','inline-progress-collapsed.png','stream-whitespace.png','stream-next-line.png','stream-empty-progress.png','sidebar-idle.png','sidebar-hover.png','workspace-collapsed.png','sidebar-renamed.png','sidebar-deleted.png','sidebar-focus.png','send-stop.png','send-restored.png','history-recovery.png','connection-busy.png','history-new-chat.png','history-partial-markdown.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try {
        if ($bitmap.Width -lt 900 -or $bitmap.Height -lt 600 -or $bitmap.GetPixel(50,40).A -eq 0) { throw 'Invalid desktop rendering.' }
    } finally { $bitmap.Dispose() }
}
foreach ($name in @('conversation-menu.png','workspace-menu.png','access-menu.png','access-menu-selected.png','settings-menu.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 200 -or $bitmap.Height -lt 50) { throw 'Invalid popup rendering.' } } finally { $bitmap.Dispose() }
}
foreach ($name in @('connection-guide-waiting.png','connection-guide-missing.png','connection-guide-board.png','connection-guide-progress.png','connection-guide-ready.png','connection-guide-compact.png','connection-guide-connected.png','connection-guide-disconnected.png','board-replacement-guide.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 700 -or $bitmap.Height -lt 450) { throw 'Invalid connection guide rendering.' } } finally { $bitmap.Dispose() }
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'board-replacement.png'))) { throw 'Board replacement confirmation did not render.' }
foreach ($name in @('connection-method.png','wireless-recovery.png','wireless-devices.png','wireless-network.png','wireless-network-compact.png','wireless-ready.png','wireless-compact.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $name))) { throw 'Wireless guide did not render.' }
}
foreach ($name in @('ethernet-ip-settings.png','connection-import.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 550 -or $bitmap.Height -lt 300) { throw 'Invalid connection settings popup rendering.' } } finally { $bitmap.Dispose() }
}
if ((Get-FileHash (Join-Path $destination 'chat.png')).Hash -eq (Get-FileHash (Join-Path $destination 'approval.png')).Hash) { throw 'Approval UI did not render.' }
foreach ($name in @('chat-latest.png','chat-reading-history.png','chat-message-location.png','chat-reentered-latest.png','access-approval.png','access-readOnly.png','access-fullAccess.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 900 -or $bitmap.Height -lt 600) { throw 'Invalid chat navigation rendering.' } } finally { $bitmap.Dispose() }
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'chat-message-preview.png'))) { throw 'Message preview did not render.' }
Write-Host "Self-contained desktop EXE and visual states passed: $destination"
