# ADR 0005：板子智能体与 Windows 工作区权威

状态：采纳。日期：2026-10-13。

Quantum Harness 用户选择 Windows 项目目录后，模型曾调用板子原生 write 工具，把成果存到 Linux 工作区，并口头报告成功。仅靠 system 提示不能建立执行边界；附件中的目录提示也会在首轮后丢失。

保留板子作为智能体大脑：它运行 OpenClaw、保存会话和任务状态、调用远程模型、选择工具并根据结果继续任务。APP 定向调用板子上的 quantum-harness 专用 agent，不在电脑上迁移智能体。该 agent 的工具白名单只包含受审批的 Windows 插件工具，禁用原生读写、shell、浏览器、跨 agent 委派等替代执行途径。专用 agent 不共享主 agent 的历史会话；主 agent 的现有用途保持独立。

APP 每轮传递当前 Windows 工作区，默认全部用户成果保存在该目录；选择附件不替代这个权威目录。会话路由包含 agent、对话 UUID 和规范工作区摘要，切换工作区不会复用旧目录的远端上下文。Windows 执行器仍以同一目录作为 AllowedRoots，路径越界必须拒绝，不能降级到板子文件系统。

“模型已回复”与“本地工具执行完成”分开显示。用户文件成功回执只来自本地执行器记录；没有记录不能确认文件已生成，失败或结果未知不重放写入。此策略不把板子文件存储全部禁用：智能体自身配置、会话等运行状态仍由 OpenClaw 管理，但不作为用户成果目录。

参考：[OpenClaw agent 工具策略](https://docs.openclaw.ai/tools/multi-agent-sandbox-tools)与[HTTP agent 路由](https://docs.openclaw.ai/gateway/openai-http-api)。以板子安装版本的配置 schema 与实际工具目录验收为准。
