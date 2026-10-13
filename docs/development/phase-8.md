# 第八阶段：Quantum Harness

Windows 原生 WPF APP，浅色导航和聊天布局参考维护者提供的 Codex 截图。用户在同一个窗口中聊天、选择文件、批准工具请求和查看结果。后台执行器与 SSH 隧道隐藏运行。

## 当前机器使用

保持盒子、网线和 Windows 网络共享正常，退出旧 APP 后双击当前修正版 `artifacts/publish/QuantumHarness/0.3.3/win-x64/QuantumHarness.exe`。程序自动连接；左下显示“盒子已连接”后可发送任务。无需另开执行器或审批 PowerShell 窗口。现有手动执行器必须先关闭，以避免端口冲突。运行时、执行器 DLL 和 EXE 必须作为完整目录保留。

点击加号选择 `.txt`、`.md`、`.docx` 或文本代码文件。工作区外的文件会复制到工作区，原文件不修改；文件路径随任务发送，文件内容只有在读取工具批准后返回盒子及远程模型。聊天记录保存在本机。

先“选择项目目录”后，侧栏显示该目录，每一轮对话都将它作为默认输出根传给板子；新任务和附件清空不会丢失工作区设置。项目切换隔离远端上下文，执行器仅允许访问该目录。板子上的专用智能体继续负责规划、远程模型调用和工具调度，用户成果必须通过 Windows 插件产生。原生板子读写和 shell 被配置策略拒绝。

生成文件后核对独立“本机执行器回执”与右侧记录中的绝对路径；没有本地回执时状态为“模型已回复，尚无本地执行回执”，不能仅凭模型口头成功判断文件存在。之前旧版写到板子的文件不会自动迁移或重放。

输入任务，点击发送或 Ctrl+Enter。右侧审批卡片显示工具、完整参数、不可变摘要和有效期；每个读取和写入都分别决定。批准创建不等于批准下一次读取。点击拒绝会返回拒绝结果。结果文件可从成功的执行记录打开；没有 Word 时仍能创建和读取 DOCX，外部打开需要关联程序。

停止会停止本机等待并尽力取消此执行器上待审批、排队和执行中的请求。当前版本面向单用户、单个办公执行器；不要与另一个客户端共享该执行器。它不能撤销已写入文件，也不保证终止远端模型推理。断线、停止和重启均不自动重放写入，先检查执行记录及实际文件。

## 新机器配置

需 Windows 11、OpenSSH Client，盒子运行 OpenClaw 并已部署第五、七阶段插件。配置脚本使用 PowerShell 7.4+；APP 本身无需 PowerShell 或 SDK。

1. 按 phase-7.md 配对 SSH 主机密钥、独立隧道密钥，初始化本地 admin/worker 凭据，并部署 worker 凭据到盒子插件。
2. 本阶段配置脚本针对当前 root 管理的盒子：运行 `./scripts/dev/Configure-DesktopConnection.ps1 -Profile <私有 connection.json 路径>`。SSH 可能交互询问盒子密码，脚本不保存密码。它给既有受限密钥新增 loopback 网关转发许可，启用认证入口并配置板子上的 quantum-harness agent（Windows 工具白名单、原生工具拒绝），验证后重启网关。既有 main 模型配置保留；多 agent roster 使用 explicit ownership。
3. APP 设置中选择工作目录、配对配置和网关凭据文件。当前默认使用 `%LOCALAPPDATA%/ClawBox/connections/192.168.137.2-22/connection.json` 与 `%LOCALAPPDATA%/ClawBox/desktop/gateway.secrets.json`。

凭据、聊天与设置在 `%LOCALAPPDATA%/ClawBox/`，目录 ACL 只授权当前 Windows 用户。它们以本地文件保存，未使用 DPAPI；磁盘访问权限属于部署边界。网关凭据具有 operator 权限，只用于可信单用户盒子，模型 API Key 不进入 APP。不要把这些文件复制到仓库或发布包。

## 开发与发布

```powershell
./scripts/dev/Setup-Dev.ps1
./scripts/dev/Start-Desktop.ps1
./scripts/ci/Verify.ps1
./scripts/ci/Test-DesktopPublish.ps1
```

发布目录：`artifacts/publish/QuantumHarness/win-x64/`。ZIP：`artifacts/releases/QuantumHarness-0.3.2-win-x64.zip`，附 SHA256 文件；目录中 desktop-manifest.json 记录所有文件的大小与哈希。发布脚本使用独立 staging/build，验证后替换目录，失败保留旧产物。发布前退出正在使用此目录的 APP。

首版为未签名便携包，不含 MSI、自动更新或系统托盘。目前验证 Windows x64；ARM64 构建参数可用，但未做真实 ARM64 机器验收。

## 手动办公验收

在加号中选择一个新建的测试文本，发送：“读取选定文件并概括内容”。检查 file.read 参数路径，批准；核对摘要与实际文件。再发送：“根据摘要创建一份新的 Word 文档，保留原文件”，核对 word.create 完整参数后批准。最后请求读取生成文档，批准 word.read 并核对内容。

常用文件工具现为十五项（含项目与 Word）。可以直接说“在工作区创建一个年度报告目录”“列出该目录里的文档”“搜索项目中出现登录失败的位置”“复制这份文件到归档目录”“把这个文件重命名”“把原文中的旧标题替换成新标题”。无需知道工具名称；浏览、读取和每次修改都分别审批。普通目录浏览包括 DOCX，项目清单用于源码快照，两者用途不同。

Word 排版可测试：“把 report.docx 中的‘项目报告’这一段设为标题一、宋体 18 磅、加粗并居中，直接修改原文件”。智能体先读取原件取得摘要，再申请 word.format；审批卡应没有新 outputPath 或与 path 相同。确认正文仍在，标题格式改变，并且没有多出副本。整个文档可用 scope=document；仅对指定段落可用 scope=paragraph。相同文字的多个段落都会匹配，当前不支持文字片段选区和复杂页面设置。

默认修改指定原件，原子发布前核对当前 SHA256。只有明确要求副本/另存为才创建新文件。格式操作不需要安装 Word；实际显示效果仍取决于打开文档的软件和本机字体。联网搜索按维护者要求本轮不启用。

另起任务请求创建一个全新测试文件，在审批卡点击拒绝，确认该文件不存在。点击新任务、搜索历史，关闭并重新打开 APP，确认历史保存、连接恢复，且未自动重发历史任务。

故障时先检查左下状态、盒子供电/网线/共享、已配对主机指纹、网关服务和占用端口。APP 不接管其他执行器实例，也不关闭未知进程。Windows 18790/18889 与盒子 18789/18791 均保持 loopback。

右上角“审批 / 执行（数量）”与顶部“审批与执行”均可打开右侧卡片；卡片完整参数下方有“批准这次请求”和“拒绝”。数量为零时没有待审批请求，聊天中的口头确认不等于已提交请求。新版启动检测、聊天和执行器 HTTP 客户端均绕过系统代理，避免本机连接和认证信息经过代理。

已有 APP 正在运行时可用 `./scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 发布到 `artifacts/publish/QuantumHarness/0.3.3/win-x64/`，不替换已占用目录。当前版本修正版使用这个目录；先退出旧 APP，再打开该目录的 QuantumHarness.exe，配对配置和聊天记录继续复用。更新不自动重放任务。

## 代码开发与多语言检查

点击侧栏“选择代码项目”选一个项目目录，APP 将其作为授权工作区并新建会话。用自然语言描述问题，例如：“修复加法函数，补充边界测试，并完成构建和测试”。智能体自动浏览、读取、修改代码，重新获取摘要，再分别请求构建和测试；用户逐次核对完整路径、内容与摘要并批准。已有源码更新需要最近读取的 SHA256。只读代码预览在 APP 内打开；当前版本不是完整 IDE，也不提供任意终端命令。

APP 与办公执行器的自包含发布包无需 Docker 或 .NET SDK。代码检查需要项目对应的本机运行时：Node/npm（内置测试需支持 test-isolation）、JDK 21+/Maven 或 .NET SDK。安装完成后运行：

```powershell
./scripts/dev/Setup-ProjectRuntimes.ps1
./scripts/ci/Test-ProjectRuntimes.ps1
```

首条命令检测 SDK 并将路径保存到 `%LOCALAPPDATA%/ClawBox/project-runtime.json`；也可显式传入路径，参数见脚本。APP 首次连接会自动检测，新增 SDK 后需重新运行配置脚本。第二条命令仅对仓库自带的 React、Vue、Java 测试夹具准备依赖并执行隔离构建/测试；输出位于 artifacts/project-qa/，不修改夹具源码。它需要 Node、JDK 21 与 Maven 同时存在，普通办公使用不需要执行它。

检查使用 Windows AppContainer 的源码副本，禁用网络，不能读取宿主凭据或私有文档。Node 项目须事先有可用的 node_modules；Maven/.NET 依赖须在本机离线缓存中可用。自动下载安装、开发服务器、浏览器端到端测试、联网集成测试、pnpm 符号链接布局和所有第三方构建插件尚未覆盖。Gradle 有实验性适配但未实机验证。原项目不产生 dist/target/bin 等构建产物；修改代码则通过单独审批写回原文件。

验收时核对执行记录里的 checksPassed、exitCode、testsObserved 和实际源码。不要仅凭模型回答或任务状态“成功”判断测试通过；全跳过、零测试、非零退出不能算通过。正常停止会结束检查进程树；强制杀死宿主后的临时授权清理尚需后续稳定性阶段完善。
## 配对文件存在，但独立 APP 提示找不到路径

如果配对是在打包应用中的终端完成，检查文件的真实路径：

```powershell
./scripts/dev/Get-PhysicalPath.ps1 -Path "$env:LOCALAPPDATA\ClawBox\connections\192.168.137.2-22\connection.json"
```

若结果落在某个应用的 `Packages\...\LocalCache\Local\ClawBox`，请从开始菜单打开**独立 PowerShell 7**，关闭 APP/执行器，然后把该真实 ClawBox 根目录作为显式源执行：

```powershell
./scripts/dev/Repair-DesktopState.ps1 -SourceDataRoot 'C:\Users\your-user\AppData\Local\Packages\actual-package\LocalCache\Local\ClawBox' -IncludeSdk
```

源目录必须用上一步实测结果，不能照抄示例。脚本保留原配置及当前 APP 设置/对话，不重新生成凭据或自动重放任务；目标存在不同内容时停止，不强制覆盖。旧隔离目录里的对话和默认工作区文件仍保留；按需单独导出。正常配置脚本会检测这种路径重定向并在写凭据前报错，避免再次出现。

可在独立 PowerShell 中运行完整包的连接诊断（会短暂启动执行器和 SSH；先退出已经连接的 APP；不会发送模型任务或审批工具）：

```powershell
$app = '.\artifacts\publish\QuantumHarness\0.3.3\win-x64\QuantumHarness.exe'
$report = Join-Path $env:TEMP ('quantum-connection-check-' + [guid]::NewGuid().ToString('N') + '.json')
Start-Process $app -ArgumentList @('--connection-check', ('"' + $report + '"')) -Wait
Get-Content -LiteralPath $report
```
