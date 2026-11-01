# 维护与发布

## 本机数据

`%LOCALAPPDATA%/ClawBox/executor/` 保存 SQLite 和两种开发 token。启动脚本为该目录配置当前 Windows 用户 ACL，不输出 token。不要将该目录上传仓库或打包进发布产物。

命令行启动脚本在前台运行执行器，Ctrl+C 关闭。Quantum Harness 启动自己拥有的执行器和 SSH 进程，隐藏后台窗口，并在 APP 正常退出时清理。当前没有安装 Windows 服务、计划任务或开机自启动。

## 依赖

版本在 Directory.Packages.props 统一管理。升级步骤：修改版本 → dotnet restore 更新锁文件 → 运行 Verify.ps1 → 按需要测试 Word 文档兼容性 → 提交 PR。CI 使用 locked-mode，NuGet 漏洞审计作为编译门禁。

SDK 私有安装在用户目录，global.json 固定功能带并允许补丁。CI 必须根据 global.json 安装 SDK，避免开发与构建环境不同。

普通构建集中存放在 `artifacts/build/`。每次发布使用 `artifacts/releases/build/<架构>/<编号>/` 的独立中间目录，避免受中断或旧缓存影响。

## 发布

Quantum Harness 用户安装版从 0.3.27 开始提供：运行 `scripts/release/Publish-Installer.ps1` 生成中文用户级安装器，支持卸载和保留数据的覆盖升级。构建、验证和限制见[桌面安装程序](desktop-installer.md)。安装版没有应用内更新，用户数据位置不变；发布产物当前未签名。

运行 Publish.ps1 生成 win-x64/win-arm64 自包含目录。发布前完成 Verify.ps1，使用干净提交和版本标签。产物目录不包含 token 或个人工作文档。首版目录发布不等于已签名安装包；代码签名证书、安装程序和回滚需要后续发布流水线。

发布先写入 `artifacts/releases/staging/`，验证 PE 头、运行时 JSON、私密文件排除和 SHA-256 清单后才更新 `artifacts/publish/<架构>/`。前一份目录移至 `artifacts/releases/previous/`；失败的临时目录保留以便排查。以上目录均被 Git 忽略，可由维护者定期清理。

`scripts/ci/Test-Publish.ps1` 使用无密钥的临时配置验证发布排除，再启动发布后的 EXE，验证健康端点和五个文件/DOCX 工具。配置 `appsettings.*.json` 和 `*.secrets.json` 不复制到构建或发布输出，部署参数通过环境提供。

`Publish-Desktop.ps1` 发布完整 Quantum Harness 包；`Test-DesktopPublish.ps1` 验证完整性并通过不连接真实盒子的 mock 状态渲染聊天、审批和紧凑窗口。其截图不代表真实模型验收；真实链路记录见第八阶段验证文档。桌面连接及私有凭据位置见 phase-8.md。

## 故障排查

健康端点只说明宿主可响应；业务验收使用 Invoke-Demo.ps1。
启动失败时检查 SDK、配置、重复实例、端口和工作目录。
任务失败先读取 errorCode 和审计记录，不要通过开启任意命令或关闭授权来绕过错误。
进程崩溃后运行中的操作可能已写入文件；残留任务标记 interrupted，需要用户检查结果后重新授权。
