# 未完成 Markdown 导致历史加载闪退（2026-10-15）

版本：0.3.18，直接在原主分支修复。

Windows .NET Runtime 1026 日志确认，0.3.17 在 Window.Show 加载消息模板时，MarkdownText.Render 第 32 行对短标记执行 `[2..^2]`，得到负截取长度，随后被包装为 XamlParseException 并使进程退出。本次不是进程残留，也不需要删除有效聊天 JSON。

原实现用 Regex.Split 分离标记，随后仅凭字符串首尾都是 `**` 判断为加粗。未被匹配的纯文本 `**`、`***` 同样符合首尾条件，导致越界。新版使用匹配位置，只有确实匹配的完整标记才执行格式截取；未完成及无效标记按原文显示。匹配器采用非回溯模式，null 内容视为空文本，不增加 HTML、命令或导航能力。

新增 11 项桌面回归，覆盖短标记、未闭合标记、普通文字中的标记、完整加粗/代码的格式保留、所有流式前缀、空内容和长片段。完整发布 EXE 的界面验收增加：保存包含 `**`、`***` 与未闭合内容的聊天，重新构造视图模型和真实主窗口并显示，关闭后再次加载同一历史和显示窗口。使用临时测试数据，不发送真实聊天或工具任务。

之前的 0.3.17 修复处理的是全零字节 JSON；这次处理有效 JSON 中的消息渲染异常。已有数据保护、有效备份和请求级审批继续保留。

最终 `scripts/ci/Verify.ps1` 通过：构建零警告零错误；桌面 151 项、执行器 63 项、插件 11 项、板子 Python 31 项通过；可选身份实机和 AppContainer 环境测试各跳过 1 项；状态迁移及格式验证通过。`scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 通过，截图位于 `artifacts/desktop-qa/publish-7b939f2c09024f8a82621ea45a54b84e/`，包括 `history-partial-markdown.png`。

以用户当前真实记录直接启动最终 EXE，主窗口正常打开；正常关闭退出码 0，再次启动成功并保持运行，观察期无新增 .NET 崩溃事件。所有现存聊天 JSON 的 SHA-256 前后一致，未清空、替换或删改聊天记录，也未发送模型/工具任务。证据：`artifacts/markdown-reopen-0.3.18.json`；最终运行 PID 14312。

完整包：`artifacts/publish/QuantumHarness/0.3.18/win-x64/QuantumHarness.exe`；归档：`artifacts/releases/QuantumHarness-0.3.18-win-x64.zip`。模拟流式片段与真实历史关闭/重开已验证，本次没有发送真实模型请求来模拟退出中的远端任务。
