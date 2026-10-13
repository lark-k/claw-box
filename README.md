# ClawBox · 龙虾盒子

RK3568 盒子承担智能体规划与任务管理，远程 Qwen 模型生成工具调用，Windows 执行器在用户授权范围内操作本地文件。

本仓库实现**第六至八阶段：Windows 执行器、盒子工具连接和 Quantum Harness 桌面 APP**。支持目录创建/浏览、文件读写/搜索/复制/重命名、DOCX 读取、新建、原件文字替换和段落排版，以及代码项目的隔离构建测试；包含请求级审批、持久化任务记录、取消与审计。APP 提供聊天、文件选择、完整参数审批和执行记录，后台管理执行器与 SSH 加密连接。

## 快速启动

已完成盒子配对的机器，当前可双击 `artifacts/publish/QuantumHarness/0.3.3/win-x64/QuantumHarness.exe`。便携包包含 .NET 运行时，无需 SDK；保持盒子电源、网线和网络共享开启。配置、构建与办公验收见[第八阶段运行指南](docs/development/phase-8.md)。

要求：Windows 11、PowerShell 7.4+、.NET SDK（版本见 `global.json`）。DOCX 操作不依赖安装 Microsoft Word。

连接盒子另需 Windows OpenSSH Client；开发验证需 Node.js 22+、Python 3.11+。配对、插件部署和审批操作见[第七阶段运行指南](docs/development/phase-7.md)。

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
