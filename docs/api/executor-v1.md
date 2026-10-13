# Windows Executor API v1

地址：`http://127.0.0.1:18790`。JSON UTF-8，属性 camelCase。业务请求使用 `Authorization: Bearer <token>`。不要把 token 放进 URL 或日志。

| 端点 | 凭据 | 行为 |
|---|---|---|
| GET /healthz | 无 | 基本存活与版本 |
| GET /api/v1/tools | worker | 工具 JSON Schema |
| POST /api/v1/approvals | admin | 为一个具体 ToolCall 创建批准 |
| POST /api/v1/tasks | worker | 消费批准并创建持久化任务，返回 202 |
| GET /api/v1/tasks | worker | 最近 100 个任务 |
| GET /api/v1/tasks/{id} | worker | 状态和工具结果 |
| POST /api/v1/tasks/{id}/cancel | worker | 尽力取消，不能撤销已提交的变更 |
| GET /api/v1/audit | admin | 最近 200 条审计，不包含原文或凭据 |

ToolCall：

```json
{
  "requestId": "c5f919d2-6a02-4ce2-a4c8-b5d363371af0",
  "tool": "file.write",
  "arguments": { "path": "C:\\approved-workspace\\note.txt", "content": "你好" }
}
```

管理员审批后返回 approvalId、expiresAt、requestHash；worker 提交 `{ "approvalId": "...", "call": <原 ToolCall> }`。不能修改工具或参数。CLI 演示实现见 `scripts/dev/Invoke-Demo.ps1`。

| 工具 | 参数 | 结果 |
|---|---|---|
| file.read | path | content、sha256、path |
| file.write | path、content、可选 expectedSha256 | bytes、sha256、path |
| word.read | path | 正文与表格文本 content、sha256、path |
| word.create | path、content | 新 DOCX 路径 |
| word.replace | path、outputPath、search、replacement | 新 DOCX 路径和 replacements |

路径必须是配置工作目录下的本地绝对路径，父目录必须已存在。新建操作不能覆盖既有文件；file.write 更新既有文件需要其当前 SHA-256。word.replace 输出到新文件，保留源文档。文字匹配为大小写敏感的字面值，支持跨相邻文本格式片段，不跨段落或制表符/换行/域边界；不使用正则表达式。

任务状态：queued、running、succeeded、failed、canceled、interrupted。任务结果使用 `result`，失败使用 `errorCode`；status=succeeded 只表示该工具调用完成，不代表用户的整个自然语言任务完成。

错误：401 无效凭据、403 无授权/路径拒绝、409 请求编号或文件内容冲突、413 数据过大、422 工具或参数不支持、429 队列已满。已入队工具执行失败通过任务状态体现，HTTP 查询仍返回 200。ProblemDetails 包含稳定的 `code`，不返回异常堆栈。
