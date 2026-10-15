# 聊天记录损坏导致启动闪退（2026-10-15）

版本：0.3.17，原主分支。

## 原因与数据保护

Windows Application 日志的 .NET Runtime 1026 与 Application Error 1000 证实，用户双击 0.3.15/0.3.16 后，ConversationStore.Load 读取聊天 JSON 抛出未处理的 JsonException，窗口出现前进程退出。异常为 `0x00 is an invalid start of a value`，不是端口占用或 EXE 依赖缺失。

本机索引及设置可以解析；索引指向的聊天文件长 712,792 字节，全部为零字节。未找到同身份的有效聊天副本。现存旧版 conversations.json 包含的是其他会话，不能据此声称恢复本条内容，也未自动导入这些记录。损坏原文件与索引已复制到用户私有目录 `desktop/recovery/startup-262bec8fe01a48b98515ad33fcb6ece0/`，原文件与副本 SHA-256 相同。没有删除原记录、重新生成凭据或重放历史任务。

## 修复与验证范围

逐条加载历史时，格式损坏或文件缺失先尝试同身份备份；没有有效备份时返回只读占位，保留原身份、时间及索引。占位显示“内容尚未恢复”，不能发送任务或重命名，也不会被保存操作覆盖；新建会话可继续使用。身份不一致与路径链接仍拒绝。启动入口对剩余 JSON 异常显示固定提示。

本机 JSON 保存改为独占临时文件、Flush(true)、原子替换和上一份有效 .bak。已有目标损坏时，先保留原始损坏字节，且不以坏数据覆盖有效备份。序列化失败会清理本次临时文件并保留当前文件。设计见 ADR 0012。

新增 9 项桌面回归：全零、截断、null、丢失文件；有效/无效备份；备份身份核验；保存轮换、失败清理与路径限制；以真实故障相同大小的全零文件启动视图模型并新建可用会话。发布 EXE 的界面验收新增损坏记录与新会话两种画面，执行实际历史加载。

当前损坏聊天的内容尚未恢复。该修复恢复软件可用性，并降低后续异常关机的数据丢失风险，不保证硬件故障下零损失。

## 最终验证结果

- `scripts/ci/Verify.ps1` 通过：零构建警告/错误；桌面 140 项、执行器 63 项、插件 11 项、板子 Python 31 项通过；可选实机身份及 AppContainer 环境测试各跳过 1 项。状态迁移检查与格式验证通过。
- `scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 通过完整包清单、哈希、自包含 EXE 及界面验证。截图：`artifacts/desktop-qa/publish-0c2d57f6ff354890a4e4c86038914f69/`，包括 `history-recovery.png`、`history-new-chat.png`。
- 直接启动最终 0.3.17 EXE，使用当前真实用户记录：主窗口持续打开，PID 9676，窗口句柄非零，执行器及 SSH 已启动；观察期没有新增 QuantumHarness .NET 崩溃事件。损坏文件与事前备份哈希仍相同。证据：`artifacts/history-startup-0.3.17.json`。应用保留运行，未发送模型或工具任务。
- 完整包：`artifacts/publish/QuantumHarness/0.3.17/win-x64/QuantumHarness.exe`；压缩包：`artifacts/releases/QuantumHarness-0.3.17-win-x64.zip`。
