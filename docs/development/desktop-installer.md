# Quantum Harness 安装、卸载与升级

## 用户操作

标准包的技术基线为 Windows 11 24H2（内部版本 26100）或更新版本的 x64 环境，并要求系统处于微软支持周期且安装当前补丁；家庭版/专业版建议通过 Windows 更新升级到当前提供的受支持功能版本，例如 25H2。Windows 11 21H2 / 22000 不符合要求，新安装器会直接提示。安装包包含 .NET 运行时，但不包含 Windows 系统补丁。双击 `QuantumHarness-0.3.30-win-x64-Setup.exe`，按中文向导安装。在“选择目标位置”页可修改路径或点击“浏览”选择其他本地磁盘目录，例如 `D:\Apps\Quantum Harness`；默认推荐 `%LOCALAPPDATA%\Programs\Quantum Harness`。当前用户需有目标目录的写入权限；安装器不主动申请管理员权限，不能选择磁盘根目录、UNC 路径或网络驱动器。开始菜单提供软件和卸载入口，可选桌面快捷方式；安装后默认不自动启动。

安装位置不会影响软件查找组件：APP 按自身所在目录查找配套执行器，快捷方式指向实际安装位置。聊天、设置、配对凭据及默认工作区仍在 `%LOCALAPPDATA%\ClawBox`，选择其他盘只改变程序位置，不迁移这些用户数据。

安装后启动直接进入首页，不自动弹出连接或恢复向导。用户按说明书从首页或设置中的有线/无线连接入口手动连接盒子。已有配对仍会在后台尝试连接，失败只显示状态。Windows OpenSSH 客户端缺失时按指引安装，系统组件安装、以太网配置可能需要管理员权限。普通办公无需 SDK、Python 或 Word；代码构建测试仍需项目对应运行时。

升级时下载完整新版安装包，等待任务结束并退出 APP（包括旧便携版），双击安装即可。无需先卸载，原聊天、设置、配对、凭据和工作区继续保留。同版本可修复，低版本不能覆盖高版本。安装期间不要重新打开旧便携版。新版本使用独立目录，旧版本保留至卸载，不手动用旧 EXE 访问可能已经迁移的数据。

升级和修复会自动带出原安装位置，须沿用该位置，避免旧程序和卸载记录分散。如需换盘，请先卸载旧安装，再运行安装包选择新位置；保留的用户数据可继续使用。不要直接剪切已安装的程序目录，否则系统登记和快捷方式仍可能指向旧位置。

从 Windows 设置 → 应用 → 已安装的应用 → Quantum Harness → 卸载，或开始菜单卸载入口执行。卸载前退出 APP。卸载保留 `%LOCALAPPDATA%\ClawBox` 的全部用户数据和工作区文件，重新安装可以继续使用；不会撤销板子侧授权、卸载共享 OpenSSH 或恢复网卡设置。不提供清空整个数据根目录的选项，避免误删工作区。

安装包目前未进行发布者代码签名；Windows 可能显示未知发布者。正式对外分发前接入可信代码签名并复验。不要把 SHA256 文件当作发布者身份证明。

## 构建与验证

### Windows 11 21H2 旧系统兼容包

0.3.29 增加 `QuantumHarness-0.3.30-win-x64-legacy-Setup.exe`，安装器允许内部版本 22000 起的 Windows 11，保留自选安装位置、覆盖升级和卸载入口。已安装 0.3.27、0.3.28 或标准版 0.3.29 时，先退出软件，再在原位置运行兼容安装包；无需先卸载，不清除配对或聊天。程序位于 `versions/0.3.30-legacy/`。也提供 `QuantumHarness-0.3.30-win-x64-legacy.zip`，解压完整目录后直接运行 `QuantumHarness.exe` 可先验证启动。

兼容包仅对 APP 和后台执行器关闭 CET apphost 标志，以尝试解决旧系统的启动兼容问题；会减少这两个进程的一项硬件栈保护，不修改 Windows 全局设置。标准包继续保留 CET。业务功能、运行时及原用户数据位置不变；这不等于微软恢复对旧系统的官方支持。必须在实际目标电脑验证启动、连接盒子、文件与 Word 操作后，才能认定该电脑可正常使用，不能仅凭“构建成功”作保证。

```powershell
./scripts/release/Publish-Installer.ps1 -LegacyWindows
./scripts/ci/Test-DesktopPublish.ps1 -SkipPublish -SideBySide -LegacyWindows
./scripts/ci/Test-Publish.ps1 -PackagePath ./artifacts/publish/QuantumHarness/0.3.30/win-x64-legacy/executor
# 以下生命周期测试会实际安装/卸载，只用于获授权的测试机器或 CI。
./tests/ClawBox.Installer.Tests/Test-Installer.ps1 -LegacyWindows -InstallerPath ./artifacts/releases/installers/QuantumHarness-0.3.30-win-x64-legacy-Setup.exe
```

发布清单的 `windowsCompatibility` 字段区分 `standard` 和 `legacy`。标准/兼容模式、文件清单和真实 EXE 的 CET 位必须一致；编译兼容安装器必须显式传入 `-LegacyWindows`，避免误把标准包装入放宽系统限制的安装器。同版本标准包与兼容包可在相同安装根切换，快捷方式切换到独立版本目录；不提供两者同时运行。

### 标准包

```powershell
./scripts/ci/Verify.ps1
# 自动构建自包含包，并生成安装器；只生成 win-x64。
./scripts/release/Publish-Installer.ps1
# 重用已经发布并通过清单校验的当前版本目录。
./scripts/release/Publish-Installer.ps1 -PackagePath ./artifacts/publish/QuantumHarness/0.3.30/win-x64
./scripts/ci/Test-DesktopPublish.ps1 -SkipPublish -SideBySide
# 独立产品标识：真实安装/升级/修复/降级拒绝/运行互斥/卸载/重装。
./tests/ClawBox.Installer.Tests/Test-Installer.ps1
# 额外测试真实完整安装包，安装到临时目录并渲染真实 EXE。
# 若本用户已有正式安装，测试拒绝执行，避免覆盖用户安装。
./tests/ClawBox.Installer.Tests/Test-Installer.ps1 -InstallerPath ./artifacts/releases/installers/QuantumHarness-0.3.30-win-x64-Setup.exe
```

产物：`artifacts/releases/installers/QuantumHarness-<版本>-win-x64-Setup.exe` 及 `.sha256.json`。版本来自 `Directory.Build.props`，必须与便携包清单一致；版本值需要保持适合 Windows 的数字版本。不生成更新清单、不上传 GitHub、不检查应用更新。

编译器由 `scripts/dev/Get-InnoCompiler.ps1` 下载 `Tools.InnoSetup 7.1.0` 便携包，校验固定 SHA256 后解压到用户工具缓存；这是非官方重打包，源项目和许可证见 NuGet 包。没有全局安装。可通过 `-IsccPath` 显式指定自行安装的 Inno Setup 7.1 编译器（含官方简体中文翻译）。工具升级时需审查来源并更新固定哈希。现有缓存与同用户程序采用同一信任边界。

生命周期测试只操作唯一的临时产品、临时安装目录和相应快捷方式；保留日志和用户文件哨兵供检查。真实包测试不发送模型任务，不连接板子，不打印凭据，安装前后比较原有本地记录的哈希。请先退出 APP/手动执行器，避免活跃数据自然变化导致保留检查失败。

生命周期测试会实际安装和卸载，只在明确允许的测试机器或 CI 中运行；不要在未获用户同意的开发电脑上执行。自定义路径覆盖含中文和空格的目录，升级测试检查自动复用原目录及拒绝原地迁移。

安装过程中用户取消被禁用，复制失败由安装引擎处理；独立版本目录让复制中断不覆盖旧程序。断电、磁盘耗尽及数据格式回退仍需专门故障注入验证，本阶段不宣称完全事务化的自动回滚。

## 安装完成后双击没有窗口

先直接打开安装目录 `versions/<版本>/QuantumHarness.exe`，区分错误快捷方式和程序自身启动失败。仅凭“没有反应”不能判定缺少运行库或安装失败，也不能用开发机上的渲染测试代替新电脑验收。

故障电脑可使用 Windows 自带 PowerShell 运行 `scripts/dev/Get-DesktopStartupReport.ps1`，自动定位当前用户安装登记和桌面快捷方式、校验发布文件清单，并收集最近 15 分钟里明确提到 QuantumHarness 的应用崩溃事件。默认不启动软件；添加 `-Launch` 时仅尝试正常启动一次，捕获退出码、标准错误与 .NET 主机加载跟踪，不会结束仍运行的 APP。软件正常启动时仍会遵循原有连接和记录行为。

如果系统执行策略禁止 `.ps1`，无需更改策略。可在手动双击失败后，将以下只读命令粘贴到 PowerShell 收集相关崩溃记录：

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1023,1026; StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue | Where-Object {$_.Message -match 'QuantumHarness'} | Select-Object -First 3 Id,TimeCreated,Message | Format-List
```

```powershell
# 将诊断脚本复制到故障电脑后，在脚本所在目录执行。
.\Get-DesktopStartupReport.ps1
# 如果没有可用的崩溃事件，可显式采集一次启动的主机跟踪。
.\Get-DesktopStartupReport.ps1 -Launch
# 无安装登记或需诊断特定便携包时：
.\Get-DesktopStartupReport.ps1 -ExePath 'D:\Apps\Quantum Harness\versions\0.3.29\QuantumHarness.exe'
```

脚本会输出报告目录，`startup-report.json` 记录文件缺失/校验异常、快捷方式目标、退出状态及相关 Windows 崩溃消息。报告包含本机路径，发送前请检查；脚本不读取聊天或凭据文件，不安装、不卸载、不自动修复。主机跟踪可能包含系统路径及进程环境相关信息，同样需要检查后再共享。不要默认要求用户安装开发 SDK、关闭防护软件或清空数据来试错。
