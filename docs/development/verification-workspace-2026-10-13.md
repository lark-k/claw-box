# Windows 工作区与成果位置修复验证

版本：Quantum Harness 0.3.2。分支：main。板子：RK3568 / OpenClaw 2026.10.2。

原因：附件目录只在首轮发送，后续轮次缺少当前项目根；APP 使用 main agent，该 agent 仍可调用板子原生 read/write/edit，模型把 Linux 目录当成用户成果目录。模型口头成功也没有独立本机回执。

修复：每轮 system 和 user 上下文都携带当前 Windows 工作区；agent、对话和规范目录摘要组成会话路由；执行器仍强制 AllowedRoots。APP 调用板子上的 quantum-harness agent，配置只允许十五项 Windows 插件工具，并拒绝原生文件/执行及替代途径。回复结束后显示由 Windows 任务记录产生的执行回执。智能体运行、模型调用、会话和规划继续留在板子。

验证：

- scripts/ci/Verify.ps1 通过：执行器 55、桌面 10、插件 11、板子安装配置 11 项，共 87 项通过。默认验证中多语言夹具理论测试跳过，专项记录在此前 project-qa 产物中；本轮未重复运行这些夹具。
- 覆盖连续两轮目录传递、项目切换会话隔离、目录大小写规范、无本机回执、Linux/越界路径不能确认本机生成、失败与未通过检查，以及系统代理接管 loopback 时的连接检测。
- 板子配置校验通过并重启；实际 /tools/invoke 对 quantum-harness 的 read/write/edit/exec/browser/sessions_spawn 均返回 404，测试用 Linux 文件未生成。
- Windows 文件读取和目录创建插件在专用 agent 上仍可调用，HTTP 返回 200；当时旧 APP 隧道未连接，工具结果为 outcome_unknown，未访问用户文档。工具搜索为模型端动态入口，不能用 HTTP /tools/invoke 的 tool_search 404 推断模型工具搜索不可用。
- scripts/ci/Test-DesktopPublish.ps1 -SideBySide 通过：自包含 EXE、清单哈希、发布配置排除及聊天/审批/紧凑渲染均检查。

当前修正版位于 artifacts/publish/QuantumHarness/0.3.2/win-x64/，ZIP 位于 artifacts/releases/QuantumHarness-0.3.2-win-x64.zip。旧目录未强制替换，当前已打开的旧 APP 不会自动变成新版。用户要求自行进行 APP 任务验收，本轮未继续控制用户界面或自动批准用户文件请求。

用户验收：退出旧 APP，打开 0.3.2，确认侧栏项目目录正确，等待“盒子已连接”；发起一个新任务，要求新建一个文本及子目录。审批时路径须为选择的 Windows 目录，执行后核对本机回执和资源管理器，再在同一对话要求创建第二个文件，验证目录仍不变。不得以模型答复中的 Linux 路径认定文件已经保存到电脑。
