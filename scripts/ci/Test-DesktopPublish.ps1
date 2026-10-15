#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipPublish, [switch]$SideBySide)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $SkipPublish) { & (Join-Path $repoRoot 'scripts/release/Publish-Desktop.ps1') -Runtime win-x64 -SideBySide:$SideBySide }
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$package=if ($SideBySide) { Join-Path $repoRoot "artifacts/publish/QuantumHarness/$version/win-x64" } else { Join-Path $repoRoot 'artifacts/publish/QuantumHarness/win-x64' }
& (Join-Path $repoRoot 'scripts/release/Test-DesktopPackage.ps1') -Path $package -Runtime win-x64
$destination=Join-Path $repoRoot ('artifacts/desktop-qa/publish-'+[guid]::NewGuid().ToString('N'))
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
foreach ($name in @('conversation-menu.png','workspace-menu.png','access-menu.png','access-menu-selected.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 200 -or $bitmap.Height -lt 50) { throw 'Invalid popup rendering.' } } finally { $bitmap.Dispose() }
}
foreach ($name in @('connection-guide-waiting.png','connection-guide-missing.png','connection-guide-board.png','connection-guide-progress.png','connection-guide-ready.png','connection-guide-compact.png','connection-guide-connected.png','connection-guide-disconnected.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 700 -or $bitmap.Height -lt 450) { throw 'Invalid connection guide rendering.' } } finally { $bitmap.Dispose() }
}
foreach ($name in @('ethernet-ip-settings.png','connection-import.png')) {
    $bitmap=[Drawing.Bitmap]::new((Join-Path $destination $name))
    try { if ($bitmap.Width -lt 550 -or $bitmap.Height -lt 300) { throw 'Invalid connection settings popup rendering.' } } finally { $bitmap.Dispose() }
}
if ((Get-FileHash (Join-Path $destination 'chat.png')).Hash -eq (Get-FileHash (Join-Path $destination 'approval.png')).Hash) { throw 'Approval UI did not render.' }
Write-Host "Self-contained desktop EXE and visual states passed: $destination"
