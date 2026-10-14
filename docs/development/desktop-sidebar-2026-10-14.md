# Quantum Harness 0.3.10：侧栏、菜单与权限 UI

日期：2026-10-14。

## 原因与实现

旧按钮模板在 IsKeyboardFocused 时主动画蓝色边框，WPF 系统 FocusVisualStyle 另画黑色虚线矩形。替换这两处显示逻辑，TextBox 保持固定边框和输入光标，控制项保留键盘焦点、选择和展开操作。[WPF 默认焦点样式源码](https://github.com/dotnet/wpf/blob/main/src/Themes/XAML/FocusVisual.xaml)说明虚线来自焦点装饰层。

旧会话菜单在鼠标按下时创建并赋给 ContextMenu，松开时系统还可能再次处理菜单；删除又从 MenuItem.Parent 和 PlacementTarget 当前 DataContext 获取目标。现在只在松开时处理一次，自行打开菜单，菜单项直接携带会话引用或工作区路径，打开新菜单前关闭上一个。Shift+F10/菜单键也可打开；任务、审批或运行中的工具存在时禁用管理项。确认对话框关闭弹出菜单后显示。

工作区标题使用无箭头 ToggleButton；右侧独立铅笔不参与折叠操作。ProjectGroup 在内存中复用，保存折叠状态，不改变用户记录格式。重命名只更新现有标题，不改变身份、内容和排序时间；空会话的自定义名称不会被第一条消息自动标题覆盖。

侧栏菜单和权限菜单使用统一圆角、阴影与图标。权限仍为请求批准、只读访问、完全访问；提示描述真实工作区授权范围。盾牌和触摸图标使用[微软 Segoe Fluent Icons 对照表](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)的已确认码位。按用户后续图片，停止按钮采用蓝色圆形白色方块，替换发送按钮原位置；连接阶段不显示对话停止按钮。

## 验证

桌面测试增加会话重命名持久化、身份/内容/目录保留、排序时间保留、折叠状态刷新、无效标题拒绝、运行中工具阻止管理的回归。

SidebarSmoke 使用独立临时状态及项目目录，实际渲染 MainWindow、ContextMenu 和确认窗口：连续三次右键打开、在选中其他会话时重命名/删除点击目标、最近会话重命名、重新读取持久化标题、删除后用户文件保留、移除工作区仅隐藏分组、键盘菜单、焦点无框、权限勾号、停止与发送同位置切换及真实 CancellationToken 取消。审批期间只显示一个同样的停止按钮。

悬停截图在 mock 窗口显式切换操作键可见性，不移动用户物理鼠标；默认隐藏状态及工作区铅笔的新建行为另有断言。渲染验收不连接盒子，不审批真实用户请求。

发布检查要求新增侧栏、折叠、重命名、删除、焦点、权限和停止状态截图及弹出菜单截图。完整执行 Verify.ps1，再运行 Test-DesktopPublish.ps1 -SideBySide 验证自包含发布 EXE。

最终结果：125 项测试通过，1 项多语言运行时测试按环境跳过；编译无警告、无错误。0.3.10 自包含 EXE 的原生视觉状态和交互断言全部通过，正式截图位于 `artifacts/desktop-qa/publish-3fd6a87252f24a39aab8cf76ed3fb2a7/`。已检查会话菜单、权限选中、悬停入口、焦点和停止状态的实际截图。当前运行的旧 APP 未被关闭；新版采用独立版本目录。
