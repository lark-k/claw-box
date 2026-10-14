# Quantum Harness 0.3.7 过程展示验收

日期：2026-10-14。目标为用户提供的工具记录样式：阶段说明是普通精简文字，工具调用与命令执行才带图标及折叠箭头。

视觉结果：通过。检查原生渲染 `artifacts/desktop-qa/tool-progress-037/inline-progress-collapsed.png` 与 `inline-progress-expanded.png`：阶段说明没有箭头或重复详情框；工具行带铅笔图标，构建检查行带终端图标，小箭头跟随记录文字。展开工具行显示实际工具名称与状态。运行中的精简说明与工具记录按发生顺序排列。

原生断言检查有完整原文的 agent 条目仍显示普通文字，agent/status 条目不存在可见折叠入口；实际工具行切换 ToggleButton 后正确展开/收起；工具与命令图标分别为铅笔和终端。继续检查完成后自动折叠整个处理过程、最终回复单独显示及手动回看。历史 Activities 的工具记录使用相同图标模板，旧 project.check 记录可识别为命令图标。

验证命令：`scripts/ci/Verify.ps1`、`scripts/ci/Test-DesktopPublish.ps1 -SideBySide`。截图使用隔离示例数据，不调用真实模型或修改用户文件；多语言运行时测试按环境条件跳过一项。本次仅调整展示类型与模板，不改变工具执行权限和审批行为。
