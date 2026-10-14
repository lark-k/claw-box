# Quantum Harness 0.3.6 过程条目验收

日期：2026-10-14。目标为用户提供的内联折叠示例：精简过程文字之后紧跟小箭头，没有另起的“详情”标签。

视觉结果：通过。比较原生渲染 `artifacts/desktop-qa/inline-progress-036/inline-progress-collapsed.png`、`inline-progress-expanded.png` 与参考图。摘要和箭头共用一行；折叠朝右、展开朝下；展开内容放在摘要下方。没有详情的发送状态条目只有文字，没有箭头。宽窗口与窄窗口保留摘要换行能力。此前会话中的工具记录也使用相同样式。

原生检查实际切换 ToggleButton 的勾选状态，验证 Expander 随之展开/收起；检查没有可见的“详情”标签；验证空详情箭头隐藏且不能展开。沿用处理期间展开、完成后整个过程自动折叠、手动回看的行为。仅修改展示模板，不改变调用、权限、请求审批、工作区或对话存储。

验证：`scripts/ci/Verify.ps1` 与 `scripts/ci/Test-DesktopPublish.ps1 -SideBySide`。截图使用隔离示例数据，不调用真实模型或修改用户文件。全仓库多语言隔离运行测试仍按环境条件跳过一项。
