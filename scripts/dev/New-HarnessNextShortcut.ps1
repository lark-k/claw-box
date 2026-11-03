[CmdletBinding()]
param([switch]$DesktopShortcut)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo 'artifacts/preview'
New-Item -ItemType Directory -Force $output | Out-Null
$shell = New-Object -ComObject WScript.Shell
$paths = @((Join-Path $output 'Quantum Harness Next.lnk'))
if ($DesktopShortcut) { $paths += Join-Path ([Environment]::GetFolderPath('Desktop')) 'Quantum Harness Next.lnk' }
foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) { throw "Shortcut already exists; left unchanged: $path" }
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $shortcut.Arguments = '-NoProfile -WindowStyle Hidden -File "' + (Join-Path $PSScriptRoot 'Open-HarnessNext.ps1') + '"'
    $shortcut.WorkingDirectory = $repo
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'Quantum Harness desktop preview with the Quantum model connection'
    $icon = Join-Path $repo 'src/ClawBox.Desktop/Assets/QuantumHarness.ico'
    if (Test-Path $icon) { $shortcut.IconLocation = $icon }
    $shortcut.Save()
    Write-Output $path
}
