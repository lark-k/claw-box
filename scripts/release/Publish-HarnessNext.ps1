[CmdletBinding()]
param([string]$BuildVersion)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$buildLock = [Threading.Mutex]::new($false, 'Local\QuantumHarnessNextBuild')
$buildOwned = $false
try {
try { $buildOwned = $buildLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $buildOwned = $true }
if (-not $buildOwned) { throw 'Exit the running preview or wait for the other build before packaging.' }
& python -B (Join-Path $repo 'src/Quantum.Brand/apply.py')
if ($LASTEXITCODE -ne 0) { throw 'Quantum branding failed.' }
$env:DSH_CLIENT_BUILD_PROFILE = 'quantum'
$env:DSH_CLIENT_TITLE = 'Quantum Harness'
if ($BuildVersion) { $env:DSH_DESKTOP_BUILD_VERSION = $BuildVersion }
$env:QUANTUM_STANDARD_NSIS = '1'
$env:QUANTUM_SMOKE_APPLICATION_DIRECTORY = Join-Path $env:USERPROFILE ('.qh-smoke/' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$env:QUANTUM_STAGE_SMOKE_APPLICATION = '1'
$companion = Join-Path $repo 'artifacts/publish/quantum-box-companion/win-x64'
$dotnet = & (Join-Path $repo 'scripts/dev/Get-Dotnet.ps1')
& $dotnet publish (Join-Path $repo 'src/Quantum.BoxCompanion/Quantum.BoxCompanion.csproj') -c Release -r win-x64 --self-contained true -o $companion
if ($LASTEXITCODE -ne 0) { throw 'Quantum box companion build failed.' }
$env:QUANTUM_BOX_COMPANION_DIR = $companion
$checksums = Join-Path $repo 'artifacts/electron-SHASUMS256.txt'
Invoke-WebRequest 'https://github.com/electron/electron/releases/download/v44.7.0/SHASUMS256.txt' -OutFile $checksums
$env:QUANTUM_ELECTRON_CHECKSUM_FILE = $checksums
# Never pass runtime model credentials into a distributable build.
$savedToken = $env:QUANTUM_GATEWAY_TOKEN
Remove-Item Env:QUANTUM_GATEWAY_TOKEN -ErrorAction SilentlyContinue
Push-Location (Join-Path $repo 'src/Quantum.Harness.Upstream')
try {
    $packageArgs = @('--filter', '@deepseek-ai/dsh-desktop', 'exec', 'tsx', 'scripts/package-target.ts', 'win-x64', '--unsigned')
    if ($BuildVersion) { $packageArgs += @('--build-version', $BuildVersion) }
    & npx.cmd --yes --package=node@24.9.0 --package=pnpm@11.28.5 -- pnpm @packageArgs
    if ($LASTEXITCODE -ne 0) { throw 'Quantum Harness installer build failed.' }
    $brandHash = (Get-FileHash -LiteralPath (Join-Path (Get-Location) '.dsh-build/quantum-brand.json') -Algorithm SHA256).Hash
    $stagedExecutable = Join-Path $env:QUANTUM_SMOKE_APPLICATION_DIRECTORY 'Quantum Harness.exe'
    $brandHash | Set-Content -LiteralPath "$stagedExecutable.quantum-brand.sha256" -Encoding ascii
    $source = Join-Path (Get-Location) 'apps/desktop/.desktop-build/targets/win-x64/unsigned-artifacts'
    $destination = Join-Path $repo 'artifacts/releases/harness-next'
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem $source -File | Where-Object {$_.Extension -in @('.exe', '.blockmap')} | Copy-Item -Destination $destination
    Get-ChildItem $destination -Filter '*.exe' | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
    @'
Quantum Harness Windows x64 测试安装包

双击 EXE，按向导选择目录安装，从开始菜单启动 Quantum Harness。
包含官方完整桌面运行时、工具、插件及 Office 依赖，无需自行安装 Python 或 Node.js。
已替换 Quantum 品牌及图标，官方账号登录入口改为“连接盒子”。

盒子：通过「更多 → 连接盒子」使用有线或无线连接；有线首次连接需盒子管理员密码。
无线连接自动使用电脑当前 Wi-Fi，输入 Wi-Fi 密码即可配网。新电脑可直接通过蓝牙加入，无需配对密钥。
连接成功后自动配置盒子模型；支持手动断开和重新连接。
本包不包含开发者的模型密钥、网关令牌、聊天或配对数据。

这是未签名的测试版本，正式公开发行前仍需代码签名。
SHA256SUMS.txt 用于核对安装包是否传输完整。
'@ | Set-Content -LiteralPath (Join-Path $destination '测试说明.txt') -Encoding utf8
    Get-ChildItem $destination -File | Select-Object Name,Length
} finally { Pop-Location; $env:QUANTUM_GATEWAY_TOKEN = $savedToken }
} finally { if ($buildOwned) { $buildLock.ReleaseMutex() }; $buildLock.Dispose() }
