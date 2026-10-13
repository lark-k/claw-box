#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Profile)
$ErrorActionPreference = 'Stop'
$connection = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
if ($connection.version -ne 1 -or $connection.boxHost -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$' -or
    $connection.sshUser -notmatch '^[a-z_][a-z0-9_-]*$' -or $connection.remotePort -lt 1024 -or $connection.remotePort -gt 65535 -or
    $connection.executorPort -lt 1024 -or $connection.executorPort -gt 65535 -or $connection.sshPort -lt 1 -or $connection.sshPort -gt 65535) {
    throw 'Invalid pairing profile.'
}
foreach ($path in @($connection.knownHostsFile, $connection.identityFile)) {
    if (-not [IO.Path]::IsPathFullyQualified($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing pairing files.' }
}
$fingerprints = @(& ssh-keygen -lf $connection.knownHostsFile -E sha256)
if ($LASTEXITCODE -ne 0 -or $fingerprints.Count -ne 1 -or ($fingerprints[0] -split '\s+')[1] -cne $connection.hostKeyFingerprint) {
    throw 'Pairing host key file changed.'
}
$health = Invoke-RestMethod "http://127.0.0.1:$($connection.executorPort)/healthz" -TimeoutSec 5
if ($health.service -ne 'claw-box-executor' -or $health.status -ne 'ok') { throw 'Start the Windows executor first.' }
Write-Host "SSH tunnel: board 127.0.0.1:$($connection.remotePort) -> Windows executor. Ctrl+C disconnects."
$arguments = @('-N','-T','-p',"$($connection.sshPort)",'-i',$connection.identityFile,
    '-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o',"UserKnownHostsFile=$($connection.knownHostsFile)",
    '-o','StrictHostKeyChecking=yes','-o','HostKeyAlgorithms=ssh-ed25519','-o','ExitOnForwardFailure=yes',
    '-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3','-o','ConnectTimeout=10',
    '-R',"127.0.0.1:$($connection.remotePort):127.0.0.1:$($connection.executorPort)","$($connection.sshUser)@$($connection.boxHost)")
& ssh @arguments
if ($LASTEXITCODE -ne 0) { throw 'SSH tunnel stopped. Inspect pending tasks before reconnecting; writes are not replayed.' }
