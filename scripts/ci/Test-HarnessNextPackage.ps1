[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$upstream = Join-Path $repo 'src/Quantum.Harness.Upstream'
$artifacts = Join-Path $upstream 'apps/desktop/.desktop-build/targets/win-x64/unsigned-artifacts'
# Native Windows PTY loaders have shorter path limits than Node filesystem APIs.
$short = Join-Path $env:LOCALAPPDATA 'QuantumHarnessNext/app'
New-Item -ItemType Directory -Force $short | Out-Null
& robocopy (Join-Path $artifacts 'win-unpacked') $short /E /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Could not stage the application at a normal installation-length path.' }
$env:QUANTUM_SMOKE_APPLICATION_DIRECTORY = $short
$env:DSH_DESKTOP_UNSIGNED = '1'
Push-Location $upstream
try {
    & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm --dir apps/desktop exec tsx scripts/smoke-packaged-runtime.ts --unsigned
    if ($LASTEXITCODE -ne 0) { throw 'Short-path packaged runtime validation failed.' }
    $destination = Join-Path $repo 'artifacts/releases/harness-next'
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem $artifacts -File | Where-Object {$_.Extension -in @('.exe','.blockmap')} | Copy-Item -Destination $destination
    Get-ChildItem $destination -Filter '*.exe' | Get-FileHash -Algorithm SHA256 | Select-Object Hash,Path
} finally { Pop-Location; Remove-Item Env:QUANTUM_SMOKE_APPLICATION_DIRECTORY -ErrorAction SilentlyContinue }
