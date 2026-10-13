#requires -Version 7.4
[CmdletBinding()]
param([string]$Profile = (Join-Path $env:LOCALAPPDATA 'ClawBox/connections/192.168.137.2-22/connection.json'))
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
if ($config.version -ne 1 -or $config.boxHost -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $config.sshUser -cne 'root' -or $config.sshPort -lt 1 -or $config.sshPort -gt 65535) { throw 'Require the existing root-managed board pairing profile.' }
$fingerprint=& ssh-keygen -lf $config.knownHostsFile -E sha256
if ($LASTEXITCODE -ne 0 -or ($fingerprint -split '\s+')[1] -cne $config.hostKeyFingerprint) { throw 'Pinned host key mismatch.' }
$public=$config.identityFile+'.pub'
if ((Get-Content -LiteralPath $public -Raw).Trim() -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+ clawbox-reverse-tunnel$') { throw 'Wrong tunnel public key.' }
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sshOptions=@('-o',"UserKnownHostsFile=$($config.knownHostsFile)",'-o','StrictHostKeyChecking=yes','-o','HostKeyAlgorithms=ssh-ed25519')
$destination="$($config.sshUser)@$($config.boxHost)"
$id=[guid]::NewGuid().ToString('N')
$remote=".openclaw/deployment/desktop-$id"
Write-Host 'Use the existing board login when SSH prompts. Passwords are not saved.'
& ssh -T -p $config.sshPort @sshOptions $destination "mkdir -p $remote && chmod 700 $remote"
if ($LASTEXITCODE -ne 0) { throw 'Cannot create private board staging directory.' }
& scp -P $config.sshPort @sshOptions (Join-Path $repoRoot 'scripts/board/configure-desktop.py') $public "${destination}:$remote/"
if ($LASTEXITCODE -ne 0) { throw 'Desktop configuration upload failed.' }
& ssh -T -p $config.sshPort @sshOptions $destination "bash -lc 'source ~/.openclaw/env.sh && python3 $remote/configure-desktop.py $remote/tunnel_ed25519.pub && XDG_RUNTIME_DIR=/run/user/0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/0/bus systemctl --user restart openclaw-gateway.service'"
if ($LASTEXITCODE -ne 0) { throw 'Board gateway configuration failed.' }
$state=Join-Path $env:LOCALAPPDATA 'ClawBox/desktop'
& (Join-Path $PSScriptRoot 'Set-PrivateDirectory.ps1') -Path $state
$stage=Join-Path $state "gateway-$id.tmp"
& scp -P $config.sshPort @sshOptions "${destination}:.openclaw/deployment/quantum-harness-gateway.secrets.json" $stage
if ($LASTEXITCODE -ne 0) { throw 'Gateway credential transfer failed.' }
$secret=Get-Content -LiteralPath $stage -Raw | ConvertFrom-Json
if ($secret.token -isnot [string] -or $secret.token.Length -lt 32) { throw 'Invalid gateway credential; existing credential retained.' }
Move-Item -LiteralPath $stage -Destination (Join-Path $state 'gateway.secrets.json') -Force
Write-Host 'Desktop gateway configured. Model credentials were not transferred.'
