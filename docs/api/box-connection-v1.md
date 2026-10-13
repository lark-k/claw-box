# 盒子连接协议 v1

Windows 执行器保持 `127.0.0.1:18790`。Windows 建立反向 SSH 隧道后，盒子通过 `http://127.0.0.1:18791` 调用相同 API，仍使用 worker bearer token；管理员 token 不传到盒子。

| 端点 | 凭据 | 行为 |
|---|---|---|
| POST /api/v1/requests | worker | 校验并保存 ToolCall，返回 202；不会执行 |
| GET /api/v1/requests/{requestId} | worker 或 admin | 原始请求、摘要、决定状态、关联任务和结果 |
| GET /api/v1/requests | admin | 待审批列表，仅返回 requestId、tool、requestHash、createdAt、expiresAt |
| POST /api/v1/requests/{requestId}/decision | admin | `{ "requestHash": "...", "approve": true/false }` |
| POST /api/v1/requests/{requestId}/cancel | worker | 撤销待审批请求；已批准任务尽力取消 |

请求复用 Executor v1 ToolCall。服务端产生摘要；审批客户端显示完整原始参数，再提交同一摘要。请求 ID、工具名称或任何参数改变，原审批不可复用。读取也需要授权，因为结果将返回智能体并可能进入远程模型上下文。

响应 ToolRequest：

```json
{
  "call": { "requestId": "...", "tool": "file.read", "arguments": { "path": "C:\\approved-workspace\\note.txt" } },
  "requestHash": "...",
  "status": "pending",
  "createdAt": "...",
  "expiresAt": "...",
  "task": null
}
```

请求状态为 pending、approved、rejected、expired、canceled、interrupted。approved 仅表示已批准并入队，执行成功需同时检查 `task.status == "succeeded"`。失败原因在 `task.errorCode`；成功内容在 `task.result`。

五分钟未批准变为 expired。pending 请求重启后变为 interrupted，不能再决定。已批准请求继续指向原任务，任务恢复遵循 executor-v1.md；不会重新创建任务。重复 POST 相同 ToolCall 返回原请求；同 ID 不同参数返回 409。批准事务包含审批创建、审批消费和任务入队；容量不足全部回滚，请求仍待决定。

| 智能体名称 | 执行器工具 |
|---|---|
| windows_file_read | file.read |
| windows_file_write | file.write |
| windows_word_read | word.read |
| windows_word_create | word.create |
| windows_word_replace | word.replace |

插件轮询等待本地决定和结果，不再次 POST 工具。取消、超时或断线时尝试撤销同一请求，并返回 outcome_unknown 与 requestId。不能认定文件操作失败，更不能自动使用新请求 ID 再次写入；在 Windows 查询原请求及实际文件。重新连接只恢复连接，不重放业务。
