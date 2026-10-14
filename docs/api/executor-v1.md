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
| word.replace | path、search、replacement、可选 outputPath/expectedSha256 | 修改后的路径、replacements、sha256 |
| word.format | path、expectedSha256、scope，格式字段和可选 outputPath | 修改后的路径、paragraphs、sha256 |
| directory.create | path | path、created（已有目录为 false） |
| directory.list | path | 直接子项 entries、truncated |
| file.info | path | bytes、sha256、lastWriteUtc |
| file.search | path（目录）、query | 字面文本匹配的 path/line/excerpt/sha256、truncated |
| file.copy | path、outputPath、expectedSha256 | 目标路径、bytes、sha256，源件保留 |
| file.move | path、outputPath、expectedSha256 | 目标路径、bytes、sha256，源件移走 |
| file.replace | path、search、replacement、expectedSha256 | 原件路径、replacements、bytes、sha256 |
| file.list | path（目录） | 文件相对路径、大小、sha256、snapshotSha256 |
| project.check | path（目录）、kind（build/test）、expectedSnapshotSha256 | stack、exitCode、checksPassed、testsObserved、有界 output、isolation |
| project.command | path（目录）、operation、expectedSnapshotSha256 | operation、exitCode、commandPassed（安装/Git）或 checksPassed（构建/测试）、有界 output |

路径必须是配置工作目录下的本地绝对路径。写入父目录不存在时先审批 directory.create；它支持最多 20 层缺失父目录。新建、复制与移动不能覆盖既有文件。移动仅限同一卷的单文件，不能移动目录。file.copy/move/info 上限为 MaxDocumentBytes（默认 16 MiB）。directory.list 最多 2,000 个直接子项、不递归、隐藏常见凭据名称，链接仅报告而不跟随。file.search 按项目源码清单的边界扫描 UTF-8 文本，最多 100 个行片段、每段 240 字符，大小写敏感、不使用正则；不扫描 DOCX 二进制正文。

用户指定修改文件时默认修改原件。file.write/file.replace 更新既有文本需要当前 SHA-256。word.replace/word.format 省略 outputPath 或令它等于 path 时原子更新原 DOCX，必须提供原件当前 expectedSha256；只有用户要求副本时指定不同的新 outputPath。不同输出路径不得覆盖既有文件。word.replace 的字面匹配支持跨相邻文本格式片段，不跨段落或制表符/换行/域边界；无匹配返回 search_not_found、不写入。

word.format 的 scope 是 document（整个正文/表格）或 paragraph（text 精确匹配的全部段落）。至少一个可选格式字段：bold=`"true"`/`"false"`、fontName、fontSizePt（6–72、0.5 磅步进）、alignment=left/center/right/justify、style=Normal/Title/Heading1/Heading2/Heading3，均为字符串。不匹配返回 paragraph_not_found。仅改变指定属性，保留其他属性和内容，不执行 Word 宏；当前不提供文字片段选区、页边距、页眉页脚或复杂模板排版。

任务状态：queued、running、succeeded、failed、canceled、interrupted。任务结果使用 `result`，失败使用 `errorCode`；status=succeeded 只表示该工具调用完成，不代表用户的整个自然语言任务完成。

项目检查先用 file.list 获取源码快照摘要。审批绑定同一目录、模式和摘要；源码改变返回 project_changed，必须重新浏览和审批。清单最多 2,000 文件、总计 32 MiB、单文件 4 MiB、深度 20；排除凭据和生成目录，拒绝链接。构建使用快照，不修改原目录。依赖副本最多 50,000 文件、1 GiB，离线使用本机依赖。运行限制：180 秒、2 GiB、32 个进程、输出末尾 64 KiB，Windows AppContainer 无网络权限。

`project.check` 的工具任务 succeeded 仅表示检查完成，业务通过必须读取 `checksPassed=true`。退出码为零但没有观察到真实测试通过仍不通过。模型只能选 build/test，不能提供任意命令、程序、环境或打开网络。缺少运行时、脚本、依赖或隔离权限时返回明确失败，不降级宿主执行。运行时配置位于本机私有目录，见 phase-8.md；Gradle 尚未实机验证。

错误：401 无效凭据、403 无授权/路径拒绝、409 请求编号或文件内容冲突、413 数据过大、422 工具或参数不支持、429 队列已满。已入队工具执行失败通过任务状态体现，HTTP 查询仍返回 200。ProblemDetails 包含稳定的 `code`，不返回异常堆栈。

project.command 的固定 operation 为 npm.install、npm.ci、build、test、git.status、git.diff。审批绑定最新源码快照；没有任意命令字符串或追加参数。安装使用本机 Node/npm，在独立 AppContainer 中访问 npm 官方 registry，关闭生命周期脚本，不使用用户 npmrc/凭据。支持普通 registry 依赖，不支持 workspaces、overrides、Git/file/URL/link 依赖。成功安装后发布 node_modules；npm.install 也发布 package-lock.json，npm.ci 保持锁文件。已有依赖保留在返回的 dependencyBackup 目录。Git 仅查看快照中的源码，禁用 hooks、外部 diff/textconv/fsmonitor 和子模块，禁止联网。失败须检查 commandPassed/checksPassed，不以工具任务 succeeded 代替命令成功。

插件在首次提交明确收到 4xx（408 除外）时返回 not_submitted 和受限公开错误码，表示未进入审批/执行；已提交后的查询失败、连接断开或超时仍为 outcome_unknown，不自动重放写操作。
