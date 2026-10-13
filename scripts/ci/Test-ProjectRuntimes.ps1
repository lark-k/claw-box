[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sdk = & (Join-Path $repo 'scripts/dev/Get-Dotnet.ps1')
& (Join-Path $repo 'scripts/dev/Setup-ProjectRuntimes.ps1') | Out-Host
$configuration = Join-Path $env:LOCALAPPDATA 'ClawBox/project-runtime.json'
$runtimes = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
if (-not $runtimes.nodeRoot -or -not $runtimes.javaHome -or -not $runtimes.mavenHome) { throw 'Node, JDK 21 and Maven are required for the polyglot fixture checks.' }
$stage = Join-Path $repo "artifacts/project-qa/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $configuration -Destination (Join-Path $stage 'runtime.json')
foreach ($name in @('react', 'vue', 'java')) {
    Copy-Item -LiteralPath (Join-Path $repo "tests/fixtures/projects/$name") -Destination $stage -Recurse
    $project = Join-Path $stage $name
    Push-Location $project
    try {
        if ($name -eq 'java') {
            & (Join-Path $runtimes.mavenHome 'bin/mvn.cmd') --batch-mode package
            if ($LASTEXITCODE -ne 0) { throw 'Owned Java fixture dependency preparation failed.' }
            $target = [IO.Path]::GetFullPath((Join-Path $project 'target'))
            if (-not $target.StartsWith([IO.Path]::GetFullPath($stage) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture output path.' }
            Remove-Item -LiteralPath $target -Recurse -Force
        } else {
            & (Join-Path $runtimes.nodeRoot 'node.exe') (Join-Path $runtimes.nodeRoot 'node_modules/npm/bin/npm-cli.js') ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "Owned $name fixture dependency preparation failed." }
        }
    } finally { Pop-Location }
}
$previous = $env:CLAWBOX_POLYGLOT_FIXTURES
try {
    $env:CLAWBOX_POLYGLOT_FIXTURES = $stage
    & $sdk test (Join-Path $repo 'tests/ClawBox.Executor.Tests') --no-restore --filter FullyQualifiedName~PolyglotTests --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Isolated polyglot checks failed.' }
    Write-Output "Verified results: $stage"
} finally { $env:CLAWBOX_POLYGLOT_FIXTURES = $previous }
