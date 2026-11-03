# Harness Next 开发验证：2026-11-03

环境：Windows x64，新工作区与 `codex/deepseek-harness-box` 分支。官方子模块固定 `ce7073d51254c26cd63b22a60835b8f948deb447`，Quantum 改动位于子模块外。Node 24.9.0 与 pnpm 11.28.5 通过 npx 获取，不替换系统 Node 22。

## 已通过

- 根目录 `scripts/ci/Verify.ps1`：旧桌面 196 通过、5 跳过；执行器 63 通过、1 跳过；插件 11 通过；板子 Python 70 通过；状态恢复及安装诊断通过。网关后续扩展后的专项测试另行通过，共 10 项。
- `python -B -m unittest discover -s tests/Quantum.ModelGateway.Tests -p 'test_*.py'`：10 项通过，包含实时 SSE、工具和图片请求字节透传、服务端凭据替换、路由/来源/长度限制、脱敏错误，以及 PowerShell 单元素数组配置、默认模型和配置保留。
- `scripts/dev/Test-ModelGateway.py`：用户提供的真实 Qwen 模型通过流式函数调用与工具结果往返验证，测试工具仅执行整数加法。
- 官方源码 `pnpm install --frozen-lockfile`：通过。子模块 Git 配置经适配后通过上游安装钩子；固定锁文件未改动。
- 官方源码 `pnpm run build:artifacts`：8 个构建阶段全部通过。
- 官方源码 `pnpm run build:check`：Host 和 Client 两个聚合类型检查均通过。
- 官方 Web 启动：127.0.0.1:18893 返回 HTTP 200；修复 Quantum 配置中单个文本模态被 PowerShell 展开为字符串的问题后，启动日志不再出现 provider 配置校验错误。
- 官方 Harness headless：使用隔离 home 和 Quantum 模型配置连接真实模型，返回预期测试标记。
- 官方内置工具：headless JSON 事件确认调用 `read`，结果状态 `completed`，正确返回私有开发目录中专门创建的无敏感测试文件内容。没有为该场景新增自定义文件工具。
- 仓库凭据检查：服务器模型凭据与网关客户端凭据均未出现在受 Git 管理或待添加的文件中。网关上游地址及凭据保存在仓库外私有配置中；记录不包含密钥。

## 已发现并处理

上游的 Git 安装钩子要求 worktreeConfig，而子模块 clone 默认将 core.worktree 保存在公共配置中。启动脚本 Prepare 阶段将该配置迁移到子模块的 config.worktree，并设置仓库格式与扩展，仅作用于子模块。Windows npx.ps1 在显式调用操作符与动态参数组合时错误解析命令，脚本改为 npx.cmd。

## 尚未验收

实体盒子的模型网关部署、新端口的 SSH 转发授权、设备注册与服务配额未实施。用户当前无实体盒子，后续提供 SSH 后继续。模型测试实际经过本机网关，不能记作盒子实机通过。

图片模型理解、完整办公工具生态、Quantum 品牌与连接页、安装包和更新链路尚未进行产品级验收。官方源码构建通过不等于上述功能全部验证。服务器目前使用用户指定的 HTTP 服务，未验证 HTTPS。

本阶段源码通过新分支提交，实际提交及三个远程同步状态以 Git 记录为准。

## 第二阶段：官方 Electron 桌面预览

已运行上游 `build:desktop`、安装固定 Electron、准备完整 primary-runtime；上游自带检查报告 Office 运行库版本及文档往返测试通过。保留上游源码未修改，包含其前端、Host、工具、插件与权限系统。

已添加本机双击入口、单实例保护、动态端口网关与退出清理，以及桌面和 artifacts/preview 中的快捷方式。首次实测发现 Python 命令解析返回两个可执行路径，导致启动错误弹窗；修正为明确选择第一个应用路径，并处理未启动进程清理与遗留锁。未替换原版桌面应用。

通过 computer-use 检查真实 Electron 窗口（标题为“DSH 本地构建”），验证中文首页、侧栏、新会话、原生工作区选择、聊天、模型名、插件列表和设置入口。独立 preview-workspace 中的桌面对话真实返回“Quantum 桌面模型连接成功”；随后调用上游文件工具读取 ui-test.txt，正确显示 `QUANTUM_UI_READ_OK_20261103`。没有为此次任务新增自定义文件读取工具。

插件页确认存在子智能体、网页搜索、Agent 循环、终端及多项实验插件入口。仅检查展示，没有擅自启用实验功能或修改权限与隐私设置。此检查不等于每个插件、联网服务、Office 工具或多模态功能均已通过端到端测试。窗口保持打开供用户预览。

## 盒子入口替换验证（2026-11-03）

- Quantum 注册阶段以独立连接页替代账号客户端；官方登录、余额、充值及原生欢迎引导不进入 Quantum 正常流程。
- `pnpm run build`：10 项全部通过，含最新 Host/Client 聚合类型检查。
- focused Vitest：`quantum-box.client.spec.tsx` + `config.client.spec.ts`，29 项通过。
- `scripts/ci/Verify.ps1`：通过；实体盒子测试仍按原条件跳过。
- 实际源码桌面 CDP 检查：菜单含设置/连接盒子、不含登录；弹窗有两个输入框、可保存、明确未连接、无外链。截图在 `artifacts/preview/quantum-box-connection.png`。
- 预览启动器只使用带有匹配品牌覆盖 SHA256 标记且通过打包验证的冻结程序；否则使用当前源码，避免重新打开旧版。
- 安装包尚未通过最终验证：长路径 PTY 在短安装路径下通过，但外部插件加载发生 Cordis 实例不一致，测试拒绝放行。未将旧安装包标记为本次变更的可发行版本。需继续修复此问题，再生成并验证新包。

## 可分发测试安装包验收（后续修复完成）

- 修复共享依赖实例不一致：在 Electron 子进程的首次 Host 导入前，以原生 realpath 统一 ASAR 的物理位置，并保持原主入口及 argv。Windows 打包应用目录重定向不再让外部插件加载另一个框架实例。
- 修复深安装目录覆盖安装停留：标准 NSIS 卸载模板增加长路径前缀，保留 atomicRMDir 与 restoreFiles 回滚逻辑。初次失败的覆盖测试没有被当作通过。
- 最新完整构建 10 项通过；Host 生命周期、路径及盒子界面相关测试 59 项通过；根 Verify.ps1 通过。
- 打包后原生 Node/Python、PTY、grep/glob 等载荷检查通过；插件离线安装、移除、HTTP 路由及动态安装通过；DOCX/XLSX/PPTX 转 PDF 及技能 CLI 发现通过。
- 对 7803 个应用文件完成开发模型凭据排除扫描，没有发现私有密钥或网关令牌。
- 最终安装器实测：深目录首次安装、首次启动、Quantum 品牌与连接盒子弹窗通过；不指定目录的第二次静默安装自动复用原安装位置，保留 profile、Electron 设置和工作区校验文件；卸载通过，用户数据保留。实际检查是同版本覆盖重装，不代替未来跨版本数据迁移验收。
- 安装版首次启动的诊断日志仍有一次非致命的 mandatory-status 未注册消息；未配置强制更新策略，桌面启动和菜单检查通过，没有因此出现故障弹窗。该包不提供应用内自动检查/下载更新。
- 产物：`artifacts/releases/harness-next/quantum-harness-0.2.2-alpha.2-win-x64-unsigned.exe`，295960272 字节。SHA256 `6ea678fe8fa91d498828b64da278a230dc8eede860fd6f353b191904a02893ae`。同目录包含测试说明、校验文件及 release-manifest.json。
- 这是 Windows x64 未签名测试版；首次需配置自己的模型服务，盒子端认证和转发仍待设备实机接入。没有将开发者模型密钥放进安装包。
