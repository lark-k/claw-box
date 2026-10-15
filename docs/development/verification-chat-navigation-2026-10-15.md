# 聊天定位、滚动条与权限偏好验证（2026-10-15）

版本：0.3.22，直接在主分支更新。设计与恢复边界见 [ADR 0014](../architecture/adr/0014-chat-navigation-and-access-preference.md)。

滚动条使用透明轨道、细圆角滑块，普通/悬停/拖动分别为浅灰、深灰和更深灰；去掉端点箭头，保留透明分页区域及原生 ScrollViewer 行为。显式清除 Windows 默认最小宽度，避免细滑块仍被系统撑宽。

初次进入和切换对话在布局完成后定位到最新消息。在底部时跟随新增和流式内容；向上阅读或点击消息标记后保留位置，输入框上方显示返回最新按钮。左侧标记对应当前对话每条消息，悬停显示角色、时间和有界正文预览，点击定位。预览随流式内容更新，仅为内存派生显示，不另存消息副本。

权限按钮的文字、图标与颜色来自当前权限状态：请求批准为灰色手势、只读为蓝色眼睛、完全访问为橙色盾牌。选择立即写入当前 Windows 用户已有设置，切换会话、保存设置、断开、重连和重新打开保留；未知值回退请求批准。新连接前遗留的请求不会因为恢复完全访问而自动批准，仍需手动核对；新请求保持原确切摘要、管理员审批、只读和路径校验。

新增 8 项桌面回归覆盖三种权限的立即保存与恢复、连接生命周期不重置权限、无效值回退、流式预览更新及恢复时旧 pending 请求隔离。原完全访问失败不重试、只读与凭据隔离检查继续通过。

`scripts/ci/Verify.ps1` 最终退出码 0：构建零警告零错误；桌面 169、执行器 63、插件 11、板子 Python 39 项通过。按原条件跳过两项实机桌面检查及一项 AppContainer 环境检查。状态迁移与格式检查通过。

`scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 退出码 0：完整包清单/哈希、自包含 EXE 和界面测试通过。ChatNavigationSmoke 在真实 WPF 窗口加载 36 条保存的消息，检查默认底部、无箭头窄滚动条、向上阅读位置保持、流式增长、逐消息预览与点击定位、返回最新及切换会话后定位底部；实际菜单分别选择三种权限，核验显示图标、颜色及重开保存结果。预览框实际打开并校验目标消息内容，未移动用户物理鼠标。

人工查看截图确认标记、返回箭头、预览和权限颜色布局。最终发布界面证据目录：`artifacts/desktop-qa/publish-cc835a356495448cac8edcbb1ed10391/`，包含 `chat-latest.png`、`chat-reading-history.png`、`chat-message-location.png`、`chat-message-preview.png`、`chat-reentered-latest.png` 与 `access-approval.png` / `access-readOnly.png` / `access-fullAccess.png`。

所有新界面测试使用隔离的示例历史和设置，没有修改真实用户权限或发送模型任务，未停止用户正在运行的 0.3.21。完整包：`artifacts/publish/QuantumHarness/0.3.22/win-x64/QuantumHarness.exe`；归档：`artifacts/releases/QuantumHarness-0.3.22-win-x64.zip`。
