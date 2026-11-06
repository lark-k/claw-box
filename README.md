# ClawBox · 龙虾盒子

> 当前主线基于官方 DeepSeek Harness，采用“Windows 智能体与工具 + 盒子模型网关”方案。入口、固定上游版本、验证状态与启动步骤见 [新方案开发指南](docs/development/harness-next.md)。下文保留原方案的开发与回退说明。

RK3568 盒子承担智能体规划与任务管理，远程 Qwen 模型生成工具调用，Windows 执行器在用户授权范围内操作本地文件。

本仓库实现**第六至八阶段：Windows 执行器、盒子工具连接和 Quantum Harness 桌面 APP**。支持目录创建/浏览、文件读写/搜索/复制/重命名、DOCX 读取、新建、原件文字替换和段落排版，以及代码项目的隔离构建测试；包含请求级审批、持久化任务记录、取消与审计。APP 提供聊天、文件选择、完整参数审批和执行记录，后台管理执行器与 SSH 加密连接。

## 快速启动

当前 Windows x64 测试安装包由 `scripts/release/Publish-HarnessNext.ps1` 构建，本地输出位于 `artifacts/releases/harness-next/`。安装包不会提交到 Git；在本地安装后从开始菜单启动 Quantum Harness。构建与连接盒子的说明见[新方案开发指南](docs/development/harness-next.md)。

原 WPF 方案仍保留源码用于开发与回退；相关说明见[连接引导](docs/development/connection-guide.md)及[安装与卸载](docs/development/desktop-installer.md)。

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
