#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][int]$Port, [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$TokensFile)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ClawBox\tests'))
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not $Workspace.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This automated test supervisor is restricted to isolated ClawBox test workspaces.'
}
$tokens = Get-Content -LiteralPath $TokensFile -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + $tokens.adminToken }
$workerFile = Join-Path (Split-Path $TokensFile -Parent) 'bridge-worker-token.tmp'
if (Test-Path -LiteralPath $workerFile) { throw 'Unexpected existing bridge test credential.' }
$tag = 'bridge-' + [guid]::NewGuid().ToString('N')
$expected = @(
    @{ tool='file.write'; arguments=@{path=(Join-Path $Workspace ($tag+'.txt'));content='ClawBox bridge original'} },
    @{ tool='file.read'; arguments=@{path=(Join-Path $Workspace ($tag+'.txt'))} },
    @{ tool='word.create'; arguments=@{path=(Join-Path $Workspace ($tag+'.docx'));content='ClawBox bridge original'} },
    @{ tool='word.replace'; arguments=@{path=(Join-Path $Workspace ($tag+'.docx'));outputPath=(Join-Path $Workspace ($tag+'-updated.docx'));search='original';replacement='verified'} },
    @{ tool='word.read'; arguments=@{path=(Join-Path $Workspace ($tag+'-updated.docx'))} }
)
$process = $null
try {
    [IO.File]::WriteAllText($workerFile, $tokens.workerToken, [Text.UTF8Encoding]::new($false))
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Command node).Source)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    foreach ($argument in @((Join-Path $PSScriptRoot 'Test-PluginClient.mjs'), "$Port", $workerFile, $Workspace, $tag)) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $step = 0
    $deadline = [datetime]::UtcNow.AddSeconds(45)
    while (-not $process.HasExited -and [datetime]::UtcNow -lt $deadline) {
        $pending = Invoke-RestMethod "http://127.0.0.1:$Port/api/v1/requests" -Headers $headers -TimeoutSec 5
        foreach ($summary in $pending) {
            $request = Invoke-RestMethod "http://127.0.0.1:$Port/api/v1/requests/$($summary.requestId)" -Headers $headers -TimeoutSec 5
            if ($step -ge $expected.Count -or $request.call.tool -cne $expected[$step].tool) {
                throw "Unexpected test tool '$($request.call.tool)' at step $step (expected '$($expected[$step].tool)'); refusing approval."
            }
            $actual = $request.call.arguments
            $wanted = $expected[$step].arguments
            if (@($actual.PSObject.Properties).Count -ne $wanted.Count) { throw 'Unexpected test argument count.' }
            foreach ($key in $wanted.Keys) {
                if ($actual.$key -cne $wanted[$key]) { throw 'Test parameters changed; refusing approval.' }
            }
            $decision = @{requestHash=$request.requestHash;approve=$true} | ConvertTo-Json
            Invoke-RestMethod "http://127.0.0.1:$Port/api/v1/requests/$($summary.requestId)/decision" -Headers $headers -Method Post -ContentType 'application/json' -Body $decision -TimeoutSec 5 | Out-Null
            $step++
        }
        Start-Sleep -Milliseconds 50
    }
    if (-not $process.HasExited -or $process.ExitCode -ne 0 -or $step -ne 5) { throw 'Plugin bridge smoke test failed.' }
    Write-Host $stdout.GetAwaiter().GetResult().Trim()
} finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $workerFile) { Remove-Item -LiteralPath $workerFile -Force }
}
