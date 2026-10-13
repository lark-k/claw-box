# ADR 0006：桌面配对配置必须对独立 APP 可见

状态：采纳。日期：2026-10-13。

配对配置原本由 MSIX 打包应用中的终端生成。Windows 把逻辑 `%LOCALAPPDATA%\ClawBox` 的新文件重定向至父应用的 `LocalCache\Local\ClawBox`；开发终端可以读到，用户通过资源管理器启动的 Quantum Harness 却找不到。用同一用户名或再次编译不能解决文件系统视图不同的问题。

保留既定存储位置 `%LOCALAPPDATA%\ClawBox`，不硬编码 Codex 包名、不扫描第三方凭据、不自动选择未知配对，也不迁移智能体到电脑。配置脚本在写入私有凭据前以文件句柄检查真实路径；发现重定向立即停止，要求在独立 PowerShell 中配置。现有安装使用显式源目录的 `Repair-DesktopState.ps1`，在独立 PowerShell 中恢复连接、网关凭据、执行器状态和可选 SDK。

迁移保留原文件，不重新生成密钥或 token，不覆盖冲突的目标文件，不修改当前桌面设置、对话或项目目录。只调整配对及构建配置中的已知目录引用。持有执行器实例锁后才复制 SQLite 与 WAL；执行器运行时拒绝迁移。目标 DACL 只授予当前用户访问。原隔离目录中的旧对话和默认工作区文件保留，供用户按需导出，不自动合并业务文件。

APP 缺少配对/网关文件时给出可操作的中文提示；不能降级到猜测凭据或关闭主机密钥校验。诊断入口 `--connection-check` 使用同一启动逻辑验证执行器、固定主机密钥及认证网关，退出时只关闭诊断创建的进程，不提交模型或文件任务，不审批请求。

参考：[Microsoft MSIX AppData 重定向](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)。本次 Context7 文档请求因 `fetch failed` 无法完成，Windows 行为依据 Microsoft 官方文档及真实独立进程验证。
