# Quantum Harness 0.3.15 图标接入验证

用户批准 B 方案后，将黑色双括号标记与暖白圆角底制作成正式 PNG 和多尺寸 ICO。资源位于 `src/ClawBox.Desktop/Assets/`，包含生成提示与 ICO 导出方式。

EXE 使用 `ApplicationIcon` 嵌入 ICO；主窗口通过资源 URI 设置 `Window.Icon`，侧栏应用名称前显示 PNG。连接引导等窗口沿用 EXE 图标。版本从 0.3.14 更新为 0.3.15，保留旧版便携包。

## 验证结果

- `scripts/ci/Verify.ps1`：退出码 0；Release 构建零警告、零错误，桌面测试 108 通过 / 1 跳过，执行器测试 63 通过 / 1 跳过，Node 测试 11 通过，Python 测试 31 通过；状态迁移与格式检查通过。跳过项为既有的真实板子身份检查和多语言 AppContainer 环境测试。
- `scripts/ci/Test-DesktopPublish.ps1 -SideBySide`：退出码 0；自包含完整包、清单与文件校验、真实 EXE 的界面状态检查通过。
- ICO 包含 16、20、24、32、40、48、64、96、128、256 像素十帧，主图外角透明。
- 使用 Windows `PrivateExtractIcons` 分别从发布 EXE 和源 ICO 提取 16、32、48、256 像素图标；四组逐像素一致，确认发布 EXE 已包含新图标。
- 人工查看真实 EXE 生成的 `chat.png`、`compact.png`：侧栏图标加载正常，标题完整显示，布局无重叠。

## 本地验收产物（不提交）

- APP：`artifacts/publish/QuantumHarness/0.3.15/win-x64/QuantumHarness.exe`
- 完整便携包：`artifacts/releases/QuantumHarness-0.3.15-win-x64.zip`
- 界面截图：`artifacts/desktop-qa/publish-2f060ccf52f54dd3872b93ca856cb106/`
- EXE 提取图标：`artifacts/desktop-qa/icon-0.3.15/`

本次未启动新版的真实连接流程，也未关闭正在运行的旧版 APP。使用新版时先退出旧版，再从完整包目录启动。
