# ClawBox · 龙虾盒子

RK3568 盒子承担智能体规划与任务管理，远程 Qwen 模型生成工具调用，Windows 执行器在用户授权范围内操作本地文件。

本仓库实现**第六至八阶段：Windows 执行器、盒子工具连接和 Quantum Harness 桌面 APP**。支持目录创建/浏览、文件读写/搜索/复制/重命名、DOCX 读取、新建、原件文字替换和段落排版，以及代码项目的隔离构建测试；包含请求级审批、持久化任务记录、取消与审计。APP 提供聊天、文件选择、完整参数审批和执行记录，后台管理执行器与 SSH 加密连接。

## 快速启动

Windows 11 21H2 等旧版系统可尝试专用兼容安装包：`artifacts/releases/installers/QuantumHarness-0.3.30-win-x64-legacy-Setup.exe`。该包保留业务功能和数据，使用应用级 CET 兼容处理，不修改系统设置；仍需在目标电脑完成实机验收，详见[兼容包说明](docs/development/desktop-installer.md)。

Windows 11 24H2（内部版本 26100）或更新且已安装当前补丁的受支持 x64 系统，可双击 `artifacts/releases/installers/QuantumHarness-0.3.30-win-x64-Setup.exe`，按中文向导选择安装位置后从开始菜单启动，可选择其他本地磁盘中的可写目录。支持 Windows 设置中卸载和新版安装包覆盖升级，保留聊天、配对及工作区文件；升级沿用原安装位置。详见[安装与卸载](docs/development/desktop-installer.md)。当前未接入应用内更新。

当前可双击完整包中的 `artifacts/publish/QuantumHarness/0.3.30/win-x64/QuantumHarness.exe`。启动后直接进入首页，不自动弹出连接向导；需要连接时从首页或左下角设置选择连接方式：无线模式搜索附近盒子、选择 Wi-Fi 并输入密码，无需先插网线；有线模式输入设备地址、SSH 端口和首次登录密码，按提示检查以太网 IP、网络共享并绑定。已连接时向导同步当前状态，首页及向导均可断开连接并保留配对。左下角设置菜单提供“有线连接”“无线连接”，并将“更换板子／重新绑定”单独分组；换板经独立确认后停止使用旧配对，保留聊天和工作区，再按向导绑定新板，支持新旧板相同 IP。工作区由首页选择，无需手填设备指纹。已有匹配插件和配置直接复用；成功配对后下次启动自动尝试连接，失败保留首页状态提示，按需手动打开连接向导。详细步骤见[连接引导](docs/development/connection-guide.md)。便携包包含 .NET 运行时和原生蓝牙支持，无需 SDK 或 Python；有线模式使用网线与网络共享，无线模式通过蓝牙配置 Wi-Fi。配置、构建与办公验收见[第八阶段运行指南](docs/development/phase-8.md)。聊天默认显示最新消息，支持左侧逐条预览定位与一键返回最新；权限图标、颜色随选择变化，并记住上次选择。保留侧栏交互、项目命令、精简进展、文件差异及连续 30 分钟无有效输出才超时的等待方式。

要求：Windows 11、PowerShell 7.4+、.NET SDK（版本见 `global.json`）。DOCX 操作不依赖安装 Microsoft Word。

连接盒子另需 Windows OpenSSH Client；开发验证需 Node.js 22+、Python 3.11+。配对、插件部署和审批操作见[第七阶段运行指南](docs/development/phase-7.md)。

板子端另提供无二维码/配对码的 BLE 配网与已绑定设备离线改网服务，复用专用 SSH 身份，包含 RTL8723DU 驱动安装器和工程验证客户端；桌面“无线连接”向导已接入，支持蓝牙发现、Wi-Fi 配置和已有配对换网；已保存 Wi-Fi 可免输密码，启动时自动尝试原盒子并在地址变化时通过蓝牙恢复。安装与维护见[板子无线服务](docs/development/board-wireless.md)，接口见[蓝牙配网协议](docs/api/board-network-v1.md)。首次运行新增板子协议测试前，执行 `python -m pip install -r tests/ClawBox.Board.Tests/requirements.txt`。

```powershell
# 首次安装仓库指定 SDK；SDK 放在用户目录，不放在仓库
./scripts/dev/Setup-Dev.ps1

# 默认工作目录 %LOCALAPPDATA%\ClawBox\workspace
./scripts/dev/Start-Executor.ps1
```

保持执行器窗口运行，在另一个 PowerShell 窗口执行：

```powershell
./scripts/dev/Invoke-Demo.ps1
```

演示执行文件写入、读取、Word 新建、文字替换和 Word 读取，并验证实际内容。它会创建新文件，文件名包含随机编号。

## 验证与发布

```powershell
./scripts/ci/Verify.ps1
./scripts/release/Publish.ps1 -Runtime win-x64

# 发布包完整性、配置排除和真实 EXE 验证
./scripts/ci/Test-Publish.ps1 -Runtime win-x64

# 构建 Quantum Harness 完整便携包，并验证真实 EXE 渲染
./scripts/ci/Test-DesktopPublish.ps1

# 构建当前用户安装包；依然保留便携包
./scripts/release/Publish-Installer.ps1
```

发布产物在 `artifacts/publish/`，不提交 Git。SDK、密钥和运行数据存放在 `%LOCALAPPDATA%/ClawBox/`。执行器默认监听 `127.0.0.1:18790`；不要通过配置或反向代理直接将这个本地接口暴露到局域网。

## 目录职责

```text
src/
  ClawBox.Contracts/       对外协议和任务状态
  ClawBox.Executor/        Windows 执行器、工具、授权、持久化、HTTP 接口
  ClawBox.OpenClawPlugin/  盒子工具插件，无额外 npm 依赖
  ClawBox.Desktop/         Quantum Harness 原生 WPF APP
tests/
  ClawBox.Executor.Tests/  安全边界、文件与 Word、持久化和 HTTP 测试
  ClawBox.OpenClawPlugin.Tests/  插件连接、取消与断线测试
  ClawBox.Desktop.Tests/   网关 SSE、凭据隔离、审批与本地记录测试
  ClawBox.Board.Tests/     插件安装与桌面网关配置测试
docs/
  architecture/           架构和决策记录
  api/                    接口及工具协议
  development/            开发流程、验收范围和维护指南
scripts/
  dev/                    开发环境、启动、端到端演示
  ci/                     本地和持续集成验证
  release/                可分发程序构建
.github/                  CI、依赖升级和 PR 模板
artifacts/                构建与测试产物（Git 忽略）
```

## 开发入口

- [架构与边界](docs/architecture/overview.md)
- [技术决策](docs/architecture/adr/0001-windows-executor.md)
- [HTTP 与工具协议](docs/api/executor-v1.md)
- [开发与提交规范](CONTRIBUTING.md)
- [当前验收范围](docs/development/phase-6.md)
- [盒子连接与本地审批](docs/development/phase-7.md)
- [连接协议](docs/api/box-connection-v1.md)
- [第七阶段验证记录](docs/development/verification-phase-7-2026-10-13.md)
- [维护与发布](docs/development/operations.md)
- [Quantum Harness 使用与验收](docs/development/phase-8.md)
- [桌面入口架构](docs/architecture/adr/0003-quantum-harness-desktop.md)

首次推送前填写远程仓库地址并启用分支保护；本地初始化不会自动创建远程仓库。
