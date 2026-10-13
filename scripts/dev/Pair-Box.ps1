#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$BoxHost = '192.168.137.2', [int]$SshPort = 22, [string]$SshUser = 'root',
    [Parameter(Mandatory)][string]$HostKeyFingerprint, [string]$TrustedHostKeyFile,
    [int]$RemotePort = 18791, [int]$ExecutorPort = 18790
)
$ErrorActionPreference = 'Stop'
if ($BoxHost -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$' -or $SshUser -notmatch '^[a-z_][a-z0-9_-]*$' -or
    $SshPort -lt 1 -or $SshPort -gt 65535 -or $RemotePort -lt 1024 -or $RemotePort -gt 65535 -or
    $ExecutorPort -lt 1024 -or $ExecutorPort -gt 65535) { throw 'Invalid connection configuration.' }
if ($HostKeyFingerprint -match '^[a-fA-F0-9]{64}$') {
    $HostKeyFingerprint = 'SHA256:' + [Convert]::ToBase64String([Convert]::FromHexString($HostKeyFingerprint)).TrimEnd('=')
}
if ($HostKeyFingerprint -notmatch '^SHA256:[A-Za-z0-9+/]{43}$') { throw 'Supply a trusted SHA256 fingerprint, not a fingerprint from an unverified network scan.' }
$state = Join-Path $env:LOCALAPPDATA "ClawBox\connections\$BoxHost-$SshPort"
New-Item -ItemType Directory -Path $state -Force | Out-Null
& (Join-Path $PSScriptRoot 'Set-PrivateDirectory.ps1') -Path $state
$profile = Join-Path $state 'connection.json'
if (Test-Path -LiteralPath $profile) { throw "Existing pairing: $profile. Use Connect-Box.ps1; do not replace a pairing implicitly." }
$knownHosts = Join-Path $state 'known_hosts'
$scanFile = Join-Path $state 'hostkey-scan.tmp'
try {
    $hostLabel = if ($SshPort -eq 22) { $BoxHost } else { "[$BoxHost]:$SshPort" }
    $scan = if ($TrustedHostKeyFile) { @(Get-Content -LiteralPath $TrustedHostKeyFile) } else {
        @(& ssh-keyscan -T 5 -p $SshPort -t ed25519 $BoxHost 2>$null)
    }
    $scan = @($scan | Where-Object { ($_ -split '\s+')[0] -ceq $hostLabel -and ($_ -split '\s+')[1] -ceq 'ssh-ed25519' })
    if ($scan.Count -ne 1) { throw 'Cannot scan this host key. Supply -TrustedHostKeyFile with a previously verified known_hosts entry; fingerprint matching is still required.' }
    $scan[0] | Set-Content -LiteralPath $scanFile -Encoding ascii
    $fingerprint = & ssh-keygen -lf $scanFile -E sha256
    if ($LASTEXITCODE -ne 0 -or ($fingerprint -split '\s+')[1] -cne $HostKeyFingerprint) { throw 'SSH host key mismatch. Pairing refused.' }
    Move-Item -LiteralPath $scanFile -Destination $knownHosts -Force
} finally { if (Test-Path -LiteralPath $scanFile) { Remove-Item -LiteralPath $scanFile -Force } }
$identity = Join-Path $state 'tunnel_ed25519'
if (-not (Test-Path -LiteralPath $identity)) {
    & ssh-keygen -q -t ed25519 -N '' -C 'clawbox-reverse-tunnel' -f $identity
    if ($LASTEXITCODE -ne 0) { throw 'SSH tunnel key generation failed.' }
}
$public = (Get-Content -LiteralPath ($identity + '.pub') -Raw).Trim()
if ($public -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+ clawbox-reverse-tunnel$') { throw 'Unexpected tunnel public key format.' }
# Interpolated values are validated numbers and generated public-key characters, never secrets.
$program = @"
import os,pathlib,subprocess
settings=subprocess.check_output(['/usr/sbin/sshd','-T'],text=True).splitlines()
if 'gatewayports no' not in settings or 'allowtcpforwarding yes' not in settings:
    raise SystemExit('Require GatewayPorts no and AllowTcpForwarding yes before pairing.')
root=pathlib.Path.home()/'.ssh'
root.mkdir(mode=0o700,exist_ok=True)
os.chmod(root,0o700)
path=root/'authorized_keys'
line='no-agent-forwarding,no-X11-forwarding,no-pty,no-user-rc,command="/bin/false",permitopen="127.0.0.1:$RemotePort",permitlisten="127.0.0.1:$RemotePort" $public'
existing=path.read_text() if path.exists() else ''
if '$($public.Split(' ')[1])' in existing and line not in existing.splitlines():
    raise SystemExit('This key already has different permissions. Refusing to broaden access.')
if line not in existing.splitlines():
    with path.open('a') as handle: handle.write(('\n' if existing and not existing.endswith('\n') else '')+line+'\n')
os.chmod(path,0o600)
print('Restricted tunnel key installed.')
"@
$payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($program))
$remote = 'python3 -c "import base64;exec(base64.b64decode(' + "'" + $payload + "'" + '))"'
Write-Host 'Pairing with verified host. SSH may ask for the board password; it is not saved.'
& ssh -T -p $SshPort -o "UserKnownHostsFile=$knownHosts" -o StrictHostKeyChecking=yes -o HostKeyAlgorithms=ssh-ed25519 "$SshUser@$BoxHost" $remote
if ($LASTEXITCODE -ne 0) { throw 'Board pairing failed; no connection profile was activated.' }
@{ version=1; boxHost=$BoxHost; sshPort=$SshPort; sshUser=$SshUser; hostKeyFingerprint=$HostKeyFingerprint;
   knownHostsFile=$knownHosts; identityFile=$identity; remotePort=$RemotePort; executorPort=$ExecutorPort } |
    ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
Write-Host "Paired. Profile: $profile"
