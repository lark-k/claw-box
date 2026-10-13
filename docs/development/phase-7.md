# 第七阶段：联通盒子与 Windows 执行器

目标：自然语言任务在盒子调用远程模型，生成 Windows 工具请求，经本地审批执行，实际文件结果回到智能体。

## 首次配置

Windows 需要 PowerShell 7.4+、OpenSSH Client、Node.js 22+（插件测试）；盒子沿用已部署 OpenClaw 2026.10.2 / Node 24。当前安装器针对 root 部署，要求 sshd 的 GatewayPorts no 和 AllowTcpForwarding yes。没有修改 SSH 全局配置。

开发验证还需 Python 3.11+，用于安装器配置保留和回退测试。CI 固定 Node 与 Python 版本；插件本身无 npm 依赖，不需要 npm install。

先从盒子本地、供应商或已验证 SSH 会话取得 Ed25519 主机密钥指纹。扫描获得的指纹本身不能建立信任：

```powershell
./scripts/dev/Start-Executor.ps1
```

保持窗口开启，在另一个窗口配对：

```powershell
./scripts/dev/Pair-Box.ps1 -BoxHost 192.168.137.2 -HostKeyFingerprint 'SHA256:<可信指纹>'
```

部分 Windows OpenSSH 版本的 ssh-keyscan 有 KEX 兼容问题，可增加 `-TrustedHostKeyFile <已有且已核验的known_hosts文件>`；仍须匹配可信指纹。不能使用 StrictHostKeyChecking=no。SSH 密码由控制台输入，不保存。

配对文件在 `%LOCALAPPDATA%\ClawBox\connections\192.168.137.2-22\connection.json`。专用 Ed25519 密钥只允许反向转发指定 loopback 端口，并禁用 shell、PTY、agent、X11 与用户启动脚本；本地转发也限制到该 loopback 端口。此密钥不能用于插件安装，安装使用管理登录。

```powershell
$profile = Join-Path $env:LOCALAPPDATA 'ClawBox\connections\192.168.137.2-22\connection.json'
./scripts/dev/Install-BoxPlugin.ps1 -Profile $profile
```

安装器上传仓库内四个插件文件与 worker token，配置显式插件和工具名单，验证后重启网关；不会启用 exec 或传输 admin token。代码使用版本目录，不覆盖已加载插件。配置验证失败恢复旧配置；回退副本只保存在盒子私有 deployment 目录。

## 日常运行

保持以下三个 Windows 窗口运行：

```powershell
# 窗口 1
./scripts/dev/Start-Executor.ps1

# 窗口 2
./scripts/dev/Connect-Box.ps1 -Profile $profile

# 窗口 3
./scripts/dev/Review-Requests.ps1
```

审批窗口显示完整参数与 SHA-256。输入 `APPROVE <显示的8位摘要>` 才执行，输入 REJECT 拒绝，回车保持待审批。控制字符及 Unicode 格式控制字符会转义，防止请求文本改变终端显示；中文普通文字可直接阅读。命令行是第七阶段本地审批入口，桌面界面留到第八阶段。

盒子 SSH 中：

```bash
source /root/.openclaw/env.sh
openclaw health --json
openclaw agent --agent main --thinking off --timeout 600 \
  --message '请用 windows_file_read 读取已授权工作目录内的具体 Windows 文件，并总结真实内容。'
```

提示中给出 Windows 绝对路径。Windows 默认工作目录为 `%LOCALAPPDATA%\ClawBox\workspace`；盒子 Linux 路径不能用来访问 Windows 文档。文件原文和结果会送给智能体与远程模型，审批前应查看路径和参数。

## 验收

- SSH 主机密钥不匹配时不能配对，worker 不能批准。
- Windows 与盒子转发端口都只监听 loopback。
- 未批准没有文件变更；批准后真实内容回传；拒绝不产生执行任务。
- 文件写读、Word 新建、替换到新文件、读取共五个工具贯通。
- 重复请求 ID 不重复执行，篡改摘要或参数被拒绝。
- 容量不足不消费决定；过期、取消、中断不可再批准。
- 断线明确报告未知结果，不自动重试写入；重启不重放。
- Verify.ps1、发布包运行测试及实机模型任务通过，并单独记录实机结果。

## 撤销与维护

停止隧道即可断开工具连接。彻底撤销配对时，从盒子 authorized_keys 删除匹配此专用公钥的那一行；不要删除其他 SSH 密钥。轮换 worker token 后重新安装插件使其取得新凭据，并重启执行器；admin token 无需与盒子同步。

如果电脑重新开启网络共享，原 SSH 连接可能被重置。重新连接遇到 remote port forwarding failed 时，先用盒子上的 `ss -ltnp '( sport = :18791 )'` 确认占用者；仅结束已确认属于此配对的失效 SSH 会话，不能批量终止 sshd 或其他用户连接。隧道重连后仍需检查原请求状态，不重放写入。

schema v2 不支持直接退回 v1 程序，旧程序应拒绝运行此状态库。回退程序前需恢复对应数据库版本或使用新的状态目录；不能删除任务记录后假装任务没有执行。

本阶段针对一个可信盒子和一个 Windows 用户；多设备配对、自动发现、桌面托盘、安装签名和长期故障测试属于后续阶段。
