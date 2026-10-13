# 第七阶段验证记录 · 2026-10-13

验证环境：Windows 11、.NET SDK 10.0.401、Node.js 22.20.0、Python 3.13.6；RK3568 Armbian / OpenClaw 2026.10.2 / Node 24.21.0。本阶段版本 0.2.0，SQLite schema v2。

## 自动化结果

- scripts/ci/Verify.ps1：Release 零警告、零错误，25 项 .NET 测试、9 项插件测试、5 项安装器测试全部通过，格式检查通过。
- scripts/ci/Test-Publish.ps1：win-x64 自包含 EXE、PE/JSON/SHA-256 清单与私有配置排除检查通过。
- 发布 EXE 的直接审批演示完成五个文件/DOCX 工具；真实插件客户端经待审批协议再次完成同样五个工具，读回内容一致。
- 脚本语法、Git diff 检查和源码常见密钥模式检查通过；未纳入运行状态、文档、密钥或构建产物。

## 实机结果与发现

Windows 主动连接盒子，核验已知 Ed25519 主机密钥，安装专用受限转发公钥。盒子 127.0.0.1:18791 能访问 Windows 127.0.0.1:18790，双方保持 loopback。插件成功加载五个 windows_* 工具，盒子仅收到 worker token。

模型通过 OpenClaw 发起文本写入、文本读取和 DOCX 新建，经本地审批执行成功。模型曾生成“原文替换成相同原文”的错误参数，本地拒绝后未生成输出文件；工具返回已补充明确停止、等待新指令的反馈。给出明确 search=original、replacement=verified 的新指令后，替换到新 DOCX 成功，独立检查源文件仍为原文，新文件为 ClawBox phase7 verified。

测试中发现 Windows 网络共享失效：电脑能访问模型，盒子无法访问外网。用户重新开启 WLAN→以太网共享后，盒子模型健康检查恢复 HTTP 200。共享切换使旧 SSH 会话失效；确认占用端口的会话属于本次隧道后清理并重连，没有重放写入。

最后一轮模型调用完成 windows_word_replace → windows_word_read；执行器读取任务状态 succeeded，真实正文为 ClawBox phase7 verified。OpenClaw 返回 status=ok，模型回复包含同一真实正文和一次替换结果。五个 Windows 工具均经过实机模型调用、本地具体审批及结果返回验证；参数错误的原请求没有获准执行，纠正通过新的明确指令完成。

## 维护边界

本阶段使用命令行审批，尚无第八阶段桌面界面。模型工具参数仍需用户核对，任务成功不能只看 HTTP 200 或模型自述。多设备配对、长期稳定性和安装签名未在本阶段验收；win-arm64 没有实机运行验证。远程 CI 工作流已配置，本记录只证明本地验证结果。
