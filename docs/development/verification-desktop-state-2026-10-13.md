# Quantum Harness 0.3.3：独立启动配对配置修复验证

日期：2026-10-13。开发分支：main。

用户通过资源管理器启动 APP 时报告配对 connection.json 路径不存在。以文件句柄读取真实路径，确认开发终端此前写入 MSIX 父应用的 LocalCache。独立进程访问原逻辑路径得到不存在，复现了截图问题。

从显式源目录恢复配对、网关/执行器凭据、停止状态的 SQLite/WAL 及本机 SDK 到真正的 `%LOCALAPPDATA%\ClawBox`。迁移前后私钥、known_hosts、两类 token 文件及数据库文件字节一致；固定主机密钥保留，当前工作区仍为用户选择的项目目录。当前 APP 设置/对话保留；原隔离目录中的历史与文件也保留。没有打印密钥或提交模型任务。

验证结果：

- `scripts/ci/Verify.ps1` 通过：执行器 55、桌面 15、插件 11、板子 Python 11 项测试；默认多语言集成测试 1 项跳过。
- CI 新增迁移检查通过：活动执行器锁拒绝迁移、目标冲突拒绝覆盖、重复迁移幂等、凭据/主机 pin 保留、SDK/SSH 引用更新、当前设置及对话保留、私有 DACL。
- 在真实 MSIX 终端中配置目录，路径重定向检查明确拒绝，验证了预防措施。
- 最终自包含包 0.3.3 的完整性、私有文件排除及聊天/审批/紧凑窗口渲染验证通过。
- 使用 Windows 文档规定的桌面进程策略启动独立诊断进程，最终发布 EXE 的 `--connection-check` 返回 success=true：读取恢复后的配置，校验 SSH 主机 pin，启动执行器，认证网关探测成功。诊断退出时其后台进程全部结束。

日志位于 Git 忽略的 `artifacts/verify-state-033-release.log`、`artifacts/desktop-publish-033-final.log` 与 `artifacts/connection-check-033-final.json`；记录不包含凭据。

本次验证覆盖独立启动与连接恢复。自然语言办公/代码任务由用户在 APP 中自行验收；本次未提交或批准真实办公任务。板子继续承担智能体规划、会话及远程模型调用。
