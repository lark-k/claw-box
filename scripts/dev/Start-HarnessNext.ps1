[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Model,
    [ValidateRange(1,65535)][int]$GatewayPort = 18792,
    [ValidateRange(1,65535)][int]$WebPort = 18893,
    [ValidateSet('Web','Desktop')][string]$Surface = 'Web',
    [switch]$Prepare,
    [switch]$ConfigureOnly,
    [switch]$ImageInput,
    [switch]$DisableThinking,
    [switch]$NoOpen
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$upstream = Join-Path $repo 'src/Quantum.Harness.Upstream'
$expected = 'ce7073d51254c26cd63b22a60835b8f948deb447'
$actual = & git -C $upstream rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $expected) { throw 'Initialize the pinned submodule: git submodule update --init --recursive' }
if (-not $env:QUANTUM_GATEWAY_TOKEN) { throw 'Set QUANTUM_GATEWAY_TOKEN to the paired gateway credential (not the server model key).' }
$state = Join-Path $env:LOCALAPPDATA 'QuantumHarnessNext'
$env:DSH_HOME = Join-Path $state 'harness'
$env:DSH_DESKTOP_USER_DATA_DIR = Join-Path $state 'electron'
$env:DSH_DESKTOP_OPEN_DEVTOOLS = '0'
$env:DSH_CLIENT_BUILD_PROFILE = 'quantum'
$env:DSH_CLIENT_TITLE = 'Quantum Harness'
$profile = if ($Surface -eq 'Web') { 'web' } else { 'desktop' }
$profileRoot = Join-Path $env:DSH_HOME "profiles/$profile"
New-Item -ItemType Directory -Force $profileRoot | Out-Null
$patch = Join-Path $profileRoot 'cordis.patch.yml'
if (-not (Test-Path $patch)) {
    [string[]]$inputs = if ($ImageInput) { @('text','image') } else { @('text') }
    $compat = @{supportsDeveloperRole=$false;maxTokensField='max_tokens'}
    if ($DisableThinking) { $compat.thinkingFormat='chat-template'; $compat.chatTemplateKwargs=@{enable_thinking=$false} }
    # JSON is valid YAML. Keep the credential as an environment reference only.
    $overlay = @(@{id='llm-pi-ai';config=@{providers=@{'quantum-box'=@{
        displayName='Quantum Box';apiKeyEnv='QUANTUM_GATEWAY_TOKEN';api='openai-completions';
        baseURL="http://127.0.0.1:$GatewayPort/v1";
        compat=$compat;
        models=@(@{id=$Model;input=$inputs})
    }}}}, @{id='agent-default-model';config=@{provider='quantum-box';model=$Model}})
    ConvertTo-Json -InputObject $overlay -Depth 12 | Set-Content -Encoding utf8 $patch
}
Write-Output "Quantum profile configured: $patch"
if ($ConfigureOnly) { return }
Push-Location $upstream
try {
    if ($Prepare) {
        # Upstream hooks require worktree-local config; submodule clones store core.worktree in common config.
        $gitDir = & git rev-parse --absolute-git-dir
        if ($LASTEXITCODE -ne 0) { throw 'Submodule Git directory unavailable.' }
        & git config --file "$gitDir/config.worktree" core.worktree $upstream
        if ($LASTEXITCODE -ne 0) { throw 'Submodule worktree configuration failed.' }
        & git config --file "$gitDir/config" --unset core.worktree
        if ($LASTEXITCODE -notin @(0,5)) { throw 'Submodule common configuration failed.' }
        & git config --file "$gitDir/config" core.repositoryFormatVersion 1
        if ($LASTEXITCODE -ne 0) { throw 'Submodule repository format configuration failed.' }
        & git config --file "$gitDir/config" extensions.worktreeConfig true
        if ($LASTEXITCODE -ne 0) { throw 'Submodule extension configuration failed.' }
        & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw 'Upstream dependency installation failed.' }
        & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm run build:artifacts
        if ($LASTEXITCODE -ne 0) { throw 'Upstream build failed.' }
        if ($Surface -eq 'Desktop') {
            & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm run build:desktop
            if ($LASTEXITCODE -ne 0) { throw 'Upstream desktop build failed.' }
        }
    }
    $command = if ($Surface -eq 'Web') { 'start:web' } else { 'start:desktop' }
    $launchArguments = @()
    if ($Surface -eq 'Web') {
        $launchArguments = @('--port', "$WebPort")
        if ($NoOpen) { $launchArguments += '--no-open' }
    }
    & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm run $command @launchArguments
    if ($LASTEXITCODE -ne 0) { throw 'Upstream launch failed.' }
} finally { Pop-Location }
