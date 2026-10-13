# 桌面聊天入口 v1

APP 通过受限 SSH 密钥转发本机 `127.0.0.1:18889` 到盒子 `127.0.0.1:18789`，不增加公网入口。反向执行器转发仍为盒子 18791 到 Windows 18790。

使用 OpenClaw `POST /v1/chat/completions`、Bearer 网关 token、`model: openclaw:quantum-harness`、`stream: true`，明确指定板子上的专用 agent。会话键为 `agent:quantum-harness:desktop:<对话 UUID>:workspace:<规范目录摘要>`，user 与 x-openclaw-session-key 保持一致；目录改变时分离远端上下文。每轮都发送固定约束、当前 Windows 工作区和本轮用户消息，不依赖会在首轮后清空的附件列表。

板子上的 agent 仍调用远程模型、搜索/选择 Windows 工具、处理结果并继续任务；配置层只允许十五项 Windows 插件工具，拒绝原生 read/write/edit/exec 及其他代替执行途径。提示不代替配置策略和本机审批。用户成果的默认输出根是所选 Windows 工作区，执行器用相同 AllowedRoots 强制约束；盒子只能持有智能体运行状态。

前文由该工作区的网关会话保存。固定约束指导模型先 tool_search 发现延迟加载的 Windows 工具，再按描述使用 tool_call，并在拒绝或未知结果后停止。此入口由板子智能体调用模型及工具，APP 不直接调用模型或把管理员凭据交给它。

接收 SSE `data:` JSON 的 `choices[].delta.content`，直到 `[DONE]`。缺少结束标记、HTTP 错误、超时或断线均作为结果未知处理，不自动重试。本地历史保存用户输入和已结束回复；异常退出可能丢失尚未结束的部分回复。重新打开对话不会发送历史请求。

审批使用 box-connection-v1.md 的独立 admin 接口。每张卡片保存服务端完整 ToolRequest，显示参数并提交原 requestHash；不可把模型答复作为执行成功证明。读取的文件内容会进入盒子及远程模型上下文，应在审批时核对。

回复结束后 APP 查询本轮 Windows 任务记录并显示独立本机回执；任务失败、检查未通过、无本地回执或回执路径在工作区外均不能确认生成本地文件。当前客户端与执行器为单用户，回执按本轮起始时间筛选；不与其他并发客户端共享执行器。
