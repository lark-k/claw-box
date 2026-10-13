#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path $repo ('artifacts/state-repair-test-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$destination = Join-Path $root 'destination'
$repair = Join-Path $repo 'scripts/dev/Repair-DesktopState.ps1'
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    foreach ($folder in @('connections/fixture-22', 'desktop', 'executor', 'sdk')) {
        & (Join-Path $repo 'scripts/dev/Set-PrivateDirectory.ps1') -Path (Join-Path $source $folder)
    }
    $profile = Join-Path $source 'connections/fixture-22/connection.json'
    @{ version=1; identityFile=(Join-Path $source 'connections/fixture-22/tunnel_ed25519'); knownHostsFile=(Join-Path $source 'connections/fixture-22/known_hosts'); hostKeyFingerprint='fixture-pin' } | ConvertTo-Json | Set-Content -LiteralPath $profile
    'fixture-private-key' | Set-Content -LiteralPath (Join-Path $source 'connections/fixture-22/tunnel_ed25519')
    'fixture-host-key' | Set-Content -LiteralPath (Join-Path $source 'connections/fixture-22/known_hosts')
    '{"token":"fixture-gateway-token"}' | Set-Content -LiteralPath (Join-Path $source 'desktop/gateway.secrets.json')
    '{"adminToken":"fixture-admin-token","workerToken":"fixture-worker-token"}' | Set-Content -LiteralPath (Join-Path $source 'executor/tokens.secrets.json')
    'fixture-database' | Set-Content -LiteralPath (Join-Path $source 'executor/executor.sqlite')
    'fixture-journal' | Set-Content -LiteralPath (Join-Path $source 'executor/executor.sqlite-wal')
    'fixture-sdk' | Set-Content -LiteralPath (Join-Path $source 'sdk/dotnet.exe')
    @{ dotnetRoot=(Join-Path $source 'sdk'); javaHome='C:\external-jdk' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $source 'project-runtime.json')
    & (Join-Path $repo 'scripts/dev/Set-PrivateDirectory.ps1') -Path (Join-Path $destination 'desktop')
    '{"workspace":"D:\\chosen-project"}' | Set-Content -LiteralPath (Join-Path $destination 'desktop/settings.json')
    'existing-user-history' | Set-Content -LiteralPath (Join-Path $destination 'desktop/conversations.json')
    $lease = [IO.File]::Open((Join-Path $source 'executor/executor.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $blocked=$false
        try { & $repair -SourceDataRoot $source -DestinationDataRoot $destination -IncludeSdk } catch { $blocked=$true }
        Require $blocked 'Live executor state must not be copied.'
        Require (-not (Test-Path (Join-Path $destination 'connections'))) 'A rejected migration must not copy pairing files.'
    } finally { $lease.Dispose() }
    & $repair -SourceDataRoot $source -DestinationDataRoot $destination -IncludeSdk
    & $repair -SourceDataRoot $source -DestinationDataRoot $destination -IncludeSdk
    $migrated = Get-Content (Join-Path $destination 'connections/fixture-22/connection.json') -Raw | ConvertFrom-Json
    Require ($migrated.hostKeyFingerprint -eq 'fixture-pin') 'Host pin must be retained.'
    Require ($migrated.identityFile -eq (Join-Path $destination 'connections/fixture-22/tunnel_ed25519')) 'SSH references must match the physical destination.'
    foreach ($relative in @('connections/fixture-22/tunnel_ed25519', 'desktop/gateway.secrets.json', 'executor/tokens.secrets.json', 'executor/executor.sqlite', 'executor/executor.sqlite-wal')) {
        Require ((Get-FileHash (Join-Path $source $relative)).Hash -eq (Get-FileHash (Join-Path $destination $relative)).Hash) 'Existing credentials and inactive database must remain byte-identical.'
    }
    Require ((Get-Content (Join-Path $destination 'project-runtime.json') -Raw | ConvertFrom-Json).dotnetRoot -eq (Join-Path $destination 'sdk')) 'SDK references must remain usable.'
    Require ((Get-Content (Join-Path $destination 'desktop/conversations.json') -Raw).Trim() -eq 'existing-user-history') 'Current user history must not be overwritten.'
    Require ((Get-Content (Join-Path $destination 'desktop/settings.json') -Raw | ConvertFrom-Json).workspace -eq 'D:\chosen-project') 'Selected workspace must be retained.'
    $acl = Get-Acl (Join-Path $destination 'executor')
    Require $acl.AreAccessRulesProtected 'Credential directory must use a private DACL.'
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) { Require ($rule.IdentityReference.Value -eq $sid) 'Only the current user may access credentials.' }
    'different-existing-key' | Set-Content (Join-Path $destination 'connections/fixture-22/tunnel_ed25519')
    $blocked=$false
    try { & $repair -SourceDataRoot $source -DestinationDataRoot $destination } catch { $blocked=$true }
    Require $blocked 'Conflicting credentials must not be overwritten.'
    Require ((Get-Content (Join-Path $destination 'connections/fixture-22/tunnel_ed25519') -Raw).Trim() -eq 'different-existing-key') 'Existing key must survive a conflict.'
    Write-Host 'Desktop state migration checks passed (locks, pinning, credential identity, references, history, DACL and conflicts).'
} finally {
    $absolute = [IO.Path]::GetFullPath($root)
    if (-not $absolute.StartsWith((Join-Path $repo 'artifacts') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
