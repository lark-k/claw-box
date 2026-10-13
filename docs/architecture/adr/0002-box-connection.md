# ADR 0002：SSH 反向隧道与 Windows 请求审批

状态：采纳。日期：2026-10-13。

## 背景

盒子上的 OpenClaw 需要调用 Windows 文件和 DOCX 工具。执行器必须保持 loopback-only，盒子不能获得本地管理员凭据，每个读取和写入均需确认具体请求。

## 决策

Windows 使用 OpenSSH 主动连接已核验主机密钥的盒子，通过 `-R 127.0.0.1:18791:127.0.0.1:18790` 转发本机执行器。盒子侧必须确认 sshd 的 GatewayPorts 为 no；隧道不开放公网或局域网监听。连接失败停止，不自动重启或重新提交工具请求。SSH 主机密钥和登录密钥存放于用户目录，配对必须提供从可信来源取得的主机密钥指纹，不能自动信任扫描结果。

盒子只持有 worker token。OpenClaw 工具插件通过隧道提交不可变 ToolCall，轮询审批和任务状态。Windows 命令行审批客户端展示完整 JSON（控制字符转义），用户批准具体摘要或拒绝；管理员凭据只由本地客户端使用。批准和创建执行任务在同一 SQLite 事务中完成，复用原有一次性审批与任务去重规则。不给盒子自动审批能力。

存储 schema 从 v1 事务迁移到 v2，新增请求表；最多 100 个待审批请求，有效期五分钟。重启使未决定请求 interrupted，已入队任务沿用第六阶段恢复规则。请求 ID 重试只能查询原请求，参数变化返回冲突；拒绝、过期、中断的请求不能再次批准。断线后的未确认结果需要按原请求编号检查，不能换编号自动重试写入。

插件仅注册五个工具，使用 OpenClaw 核心 `api.registerTool` 接口，无额外 npm 依赖。路径校验、输入上限、文件变更规则继续由 Windows 执行器负责。桌面聊天与审批界面留到第八阶段。

## 影响

开发运行需要执行器、SSH 隧道和本地审批窗口；本阶段是单盒子、单 Windows 用户的配对。SSH 凭据、worker token 可单独撤销；更换 worker token 时需更新盒子私有配置并重启插件。盒子的 root 和同一 Windows 用户是可信部署主体。默认不提供无人值守自动审批，也不承诺断线任务自动恢复。

参考：[OpenClaw 工具插件](https://docs.openclaw.ai/plugins/tool-plugins)、[OpenSSH ssh](https://man.openbsd.org/ssh)、[sshd_config GatewayPorts](https://man.openbsd.org/sshd_config#GatewayPorts)。
