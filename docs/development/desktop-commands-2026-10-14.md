# Quantum Harness 0.3.9：项目命令与空闲等待

日期：2026-10-14。

## 定位与修复

用户的作品集会话在十分钟处结束等待。旧 APP 使用整轮固定取消期限，项目检查又不能安装缺失依赖，因此模型只能要求用户自行运行 npm install。新增 windows_project_command，提供 npm.install、npm.ci、build、test、git.status、git.diff 六种固定操作，继续使用最新项目快照、Windows 路径校验及逐请求审批。

依赖安装在独立 AppContainer 中联网，仅使用官方 npm registry，关闭生命周期脚本，配置和缓存位于私有临时目录。成功安装后重新检查快照，再发布依赖；旧依赖保留备份，不自动重放未知写入。Git 状态及差异在源码、Git 元数据和可信 Git 运行时的独立副本中只读执行。

隔离构建原来向所有 Vite 版本传递 configLoader 参数，Vite 5 不支持；其默认配置打包流程也与现有同步 esbuild 适配不兼容。Vite 5 的 JavaScript 配置改为在独立构建副本中由 Node 加载，用户的 package.json 与配置文件不变。TypeScript 配置明确返回不支持，避免误报构建成功。

APP 的整轮固定期限改为 IdleDeadline：收到非空模型文字或新的工具状态时刷新单调时钟，只有连续三十分钟无实际进展才取消等待。重复轮询结果、传输心跳及界面计时不刷新。停止按钮仍立即取消，迟到进展不能恢复已取消请求。盒子 agents.defaults.timeoutSeconds 设置为 0，关闭整轮固定运行期限；单个工具仍保留各自执行期限。

## 验证

- Verify.ps1：桌面 38 项、执行器 63 项、插件 11 项、板子脚本 11 项通过；1 项多语言运行时测试因环境跳过。状态迁移检查通过。
- 启用 QH_INSTALL_LIVE 的真实 Vue/Vite 5 测试已下载依赖并完成隔离生产构建。
- npm 安装与 ci 测试检查安装钩子未执行、锁文件写入、旧快照拒绝及用户源码保留。Git 状态/差异的真实仓库测试通过。
- 模拟五次间隔二十九分钟的新进展，整轮超过两小时仍未超时；最后连续三十分钟空闲才取消。手动停止后收到进展仍保持取消。
- 已通过配对 SSH 更新盒子插件并重启网关，读回 timeoutSeconds=0、模型 maxTokens=8192；tools.catalog 确认 windows_project_command 注册和参数完整。诊断凭据未写入仓库，临时诊断隧道已关闭。

便携包使用 Test-DesktopPublish.ps1 -SideBySide 检查自包含 EXE 和原生视觉状态。
