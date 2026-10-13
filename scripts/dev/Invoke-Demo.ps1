[CmdletBinding()]
param([int]$Port = 18790, [string]$Workspace = (Join-Path $env:LOCALAPPDATA 'ClawBox\workspace'),
    [string]$TokensFile = (Join-Path $env:LOCALAPPDATA 'ClawBox\executor\tokens.secrets.json'))
$ErrorActionPreference = 'Stop'
$tokens = Get-Content -LiteralPath $TokensFile -Raw | ConvertFrom-Json
$base = "http://127.0.0.1:$Port"
$admin = @{ Authorization = "Bearer $($tokens.adminToken)" }
$worker = @{ Authorization = "Bearer $($tokens.workerToken)" }
function Invoke-ApprovedTool([string]$Tool, [hashtable]$Arguments) {
    $call = @{ requestId = [guid]::NewGuid().ToString(); tool = $Tool; arguments = $Arguments }
    $approval = Invoke-RestMethod "$base/api/v1/approvals" -Method Post -Headers $admin -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($call | ConvertTo-Json -Depth 8)))
    $request = @{ approvalId = $approval.approvalId; call = $call }
    $task = Invoke-RestMethod "$base/api/v1/tasks" -Method Post -Headers $worker -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8)))
    for ($i=0; $i -lt 100; $i++) {
        $task = Invoke-RestMethod "$base/api/v1/tasks/$($task.id)" -Headers $worker
        if ($task.status -notin @('queued','running')) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($task.status -ne 'succeeded') { throw "Task failed: $($task.errorCode), status=$($task.status)" }
    return $task.result
}
$id = [guid]::NewGuid().ToString('N').Substring(0,8)
$textPath = Join-Path $Workspace "demo-$id.txt"
$wordPath = Join-Path $Workspace "demo-$id.docx"
$outputPath = Join-Path $Workspace "demo-$id-updated.docx"
Invoke-ApprovedTool 'file.write' @{ path=$textPath; content='龙虾盒子：文件读写和 Word 测试。' } | Out-Null
$text = Invoke-ApprovedTool 'file.read' @{ path=$textPath }
Invoke-ApprovedTool 'word.create' @{ path=$wordPath; content=$text.content } | Out-Null
$replace = Invoke-ApprovedTool 'word.replace' @{ path=$wordPath; outputPath=$outputPath; search='测试'; replacement='验证成功' }
$word = Invoke-ApprovedTool 'word.read' @{ path=$outputPath }
if ($replace.replacements -ne 1 -or $word.content -notlike '*验证成功*') { throw 'Word verification failed.' }
Write-Host 'End-to-end file and DOCX verification passed.'
Write-Host "Output: $outputPath"
