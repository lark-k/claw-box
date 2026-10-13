#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Profile, [string]$TokensFile)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$connection = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
if ($connection.version -ne 1 -or $connection.boxHost -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$' -or
    $connection.sshUser -cne 'root' -or $connection.sshPort -lt 1 -or $connection.sshPort -gt 65535 -or
    $connection.remotePort -lt 1024 -or $connection.remotePort -gt 65535) {
    throw 'This installer requires a valid paired root deployment.'
}
$fingerprint = & ssh-keygen -lf $connection.knownHostsFile -E sha256
if ($LASTEXITCODE -ne 0 -or ($fingerprint -split '\s+')[1] -cne $connection.hostKeyFingerprint) { throw 'Pairing host key file changed.' }
if (-not $TokensFile) { $TokensFile = Join-Path $env:LOCALAPPDATA 'ClawBox\executor\tokens.secrets.json' }
$tokens = Get-Content -LiteralPath $TokensFile -Raw | ConvertFrom-Json
if ($tokens.workerToken -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Start the executor to provision credentials first.' }
$remote = "root@$($connection.boxHost)"
$sshOptions = @('-o',"UserKnownHostsFile=$($connection.knownHostsFile)",'-o','StrictHostKeyChecking=yes',
    '-o','HostKeyAlgorithms=ssh-ed25519','-o','ConnectTimeout=10')
$id = [guid]::NewGuid().ToString('N')
$stage = "/root/.openclaw/deployment/clawbox-windows/$id"
$uploadDirectory = Join-Path (Split-Path $Profile -Parent) ('upload-' + $id)
New-Item -ItemType Directory -Path $uploadDirectory | Out-Null
$tokenFile = Join-Path $uploadDirectory 'worker-token'
try {
    # Private pairing directory ACL protects the temporary file. Only worker goes to the board.
    [IO.File]::WriteAllText($tokenFile, $tokens.workerToken, [Text.UTF8Encoding]::new($false))
    & ssh -T -p $connection.sshPort @sshOptions $remote "umask 077; mkdir -p '$stage'"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create private deployment directory.' }
    $plugin = Join-Path $repoRoot 'src\ClawBox.OpenClawPlugin'
    $uploadPaths = @('package.json','openclaw.plugin.json','index.js','client.js') | ForEach-Object { Join-Path $plugin $_ }
    $uploadPaths += $tokenFile, (Join-Path $repoRoot 'scripts\board\install-plugin.py')
    & scp -P $connection.sshPort @sshOptions @uploadPaths ($remote + ':' + $stage + '/')
    if ($LASTEXITCODE -ne 0) { throw 'Plugin bundle upload failed.' }
    $command = "source /root/.openclaw/env.sh; python3 '$stage/install-plugin.py' '$stage' --port $($connection.remotePort) && XDG_RUNTIME_DIR=/run/user/0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/0/bus systemctl --user restart openclaw-gateway.service"
    & ssh -T -p $connection.sshPort @sshOptions $remote $command
    if ($LASTEXITCODE -ne 0) { throw 'Plugin installation failed; inspect validation output.' }
    @{ pluginVersion=(Get-Content (Join-Path $plugin 'package.json') -Raw | ConvertFrom-Json).version;
       remoteDirectory=($stage+'/plugin'); deployedAt=[datetime]::UtcNow.ToString('O') } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path (Split-Path $Profile -Parent) 'deployment.json') -Encoding utf8
    Write-Host 'Plugin deployed. Wait for gateway health, then start Connect-Box.ps1 and Review-Requests.ps1.'
} finally {
    if (Test-Path -LiteralPath $tokenFile) { Remove-Item -LiteralPath $tokenFile -Force }
    Remove-Item -LiteralPath $uploadDirectory
}
