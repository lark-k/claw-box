[CmdletBinding()]
param([string]$Workspace, [int]$Port = 18790)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet = & (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$state = Join-Path $env:LOCALAPPDATA 'ClawBox\executor'
if (-not $Workspace) { $Workspace = Join-Path $env:LOCALAPPDATA 'ClawBox\workspace' }
$Workspace = [IO.Path]::GetFullPath($Workspace)
New-Item -ItemType Directory -Path $state, $Workspace -Force | Out-Null
# The authenticated Windows user owns this local secret; never write it inside the repository.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $state -AclObject $acl
$secretFile = Join-Path $state 'tokens.secrets.json'
if (-not (Test-Path -LiteralPath $secretFile)) {
    @{ adminToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32));
       workerToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) } |
        ConvertTo-Json | Set-Content -LiteralPath $secretFile -Encoding utf8
}
$tokens = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
$env:Executor__AdminToken = $tokens.adminToken
$env:Executor__WorkerToken = $tokens.workerToken
$env:Executor__AllowedRoots__0 = $Workspace
$env:Executor__DataDirectory = $state
$env:Executor__Port = $Port
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Host "Executor: http://127.0.0.1:$Port"
Write-Host "Workspace: $Workspace"
try {
    & $dotnet run --project (Join-Path $repoRoot 'src\ClawBox.Executor') -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Executor stopped with an error.' }
}
finally {
    Remove-Item Env:Executor__AdminToken, Env:Executor__WorkerToken -ErrorAction SilentlyContinue
}
