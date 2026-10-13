#requires -Version 7.4
[CmdletBinding()]
param([int]$Port = 18790, [string]$TokensFile, [switch]$Once)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid executor port.' }
if (-not $TokensFile) { $TokensFile = Join-Path $env:LOCALAPPDATA 'ClawBox\executor\tokens.secrets.json' }
$tokens = Get-Content -LiteralPath $TokensFile -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + $tokens.adminToken }
$base = "http://127.0.0.1:$Port/api/v1"
Write-Host 'Local approval console. Reads and writes both need approval. Ctrl+C exits.'
do {
    $requests = Invoke-RestMethod "$base/requests" -Headers $headers -TimeoutSec 10
    foreach ($summary in $requests) {
        $request = Invoke-RestMethod "$base/requests/$($summary.requestId)" -Headers $headers -TimeoutSec 10
        if ($request.status -ne 'pending') { continue }
        Write-Host "Request: $($request.call.requestId) / expires $($request.expiresAt)"
        # JSON escapes controls; additionally escape Unicode formatting/bidi controls, keeping Chinese readable.
        $display = $request.call | ConvertTo-Json -Depth 20
        $display = [regex]::Replace($display, '[\p{Cf}\p{Zl}\p{Zp}]', [Text.RegularExpressions.MatchEvaluator]{
            param($match) '\u' + ([int][char]$match.Value[0]).ToString('x4')
        })
        Write-Host $display
        Write-Host "SHA-256: $($request.requestHash)"
        $confirmation = 'APPROVE ' + $request.requestHash.Substring(0,8)
        $answer = Read-Host "Type '$confirmation' to execute exactly this request; REJECT to deny; Enter to leave pending"
        if ($answer -cne $confirmation -and $answer -cne 'REJECT') { continue }
        $decision = @{ requestHash=$request.requestHash; approve=($answer -ceq $confirmation) } | ConvertTo-Json
        try {
            $result = Invoke-RestMethod "$base/requests/$($request.call.requestId)/decision" -Method Post -Headers $headers -ContentType 'application/json' -Body $decision -TimeoutSec 10
            Write-Host "Decision: $($result.status); task: $($result.task.id)"
        } catch { Write-Warning 'Decision failed or outcome is unknown. Refresh the request before attempting another action.' }
    }
    if (-not $Once) { Start-Sleep -Seconds 1 }
} while (-not $Once)
