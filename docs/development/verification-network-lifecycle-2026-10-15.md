# 网络刷新与隧道释放验证（2026-10-15）

版本：0.3.20，直接在主分支更新。

以太网检测只选实体 PCI/USB 网口，过滤蓝牙、虚拟网卡和 WFP/QoS 子接口，优先读取 Windows 保存的手动 IPv4 配置并去重。每项只显示一张网卡和一组地址/掩码。地址配置与设备可达性分开检查，密码不修改电脑网卡。

保存 IP、网络地址变化及返回共享属性窗口立即触发只读复查。已验证登录后，模型网络失败可周期重查；错误密码不会定时重试。输入版本隔离旧结果。首页连接按钮显示“停止连接”并支持取消，按钮下直接显示进度或忙碌原因。

日常 SSH 转发由进程内 SSH.NET 对象持有，沿用原专用私钥和固定 Ed25519 主机身份。退出先停止本地和远端转发、断开 SSH，再回收执行器。显式绑定中的固定恢复程序只处理已确认断开的本机旧 root 隧道；活动、其他电脑与未知占用保留。设计见 [ADR 0013](../architecture/adr/0013-network-refresh-and-stale-tunnel.md)。

用户授权连接的原配对设备上，`LiveTunnelLifecycleTests.Normal_disconnect_releases_both_listeners_for_immediate_reconnect` 连续三轮成功：建立同一组端口、确认 Alive、Dispose 两次、下一轮立即复用端口。没有发送模型任务或审批工具。只读排查曾读到与旧 APP 启动时间对应的远端 sshd 监听，而 Windows 对应源端口已不存在；随后执行恢复检查返回 free，不能声称本次实际杀掉了该会话。已有 Xshell 会话保留。

`scripts/ci/Verify.ps1` 完整通过：零警告零错误；桌面 156、执行器 63、插件 11、板子 Python 39 项通过。常规套件跳过两项需实机环境的桌面测试及一项 AppContainer 环境测试；本次额外显式运行上述真实隧道生命周期测试并通过。状态迁移与格式检查通过。Python 新增 8 项涵盖断开的本机隧道、活动连接、其他电脑、未知进程、过期/不完整证据及终止前复核。

`scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 通过完整包清单/哈希、自包含 EXE 和界面验收；首页“停止连接”截图及真实按钮点击取消断言通过。截图目录：`artifacts/desktop-qa/publish-c056e0db1cbb4c4e8a5f5dfad37cd3cb/`。

最终发布包连接诊断返回失败，随后使用固定主机身份的管理 SSH 也连接超时；用户明确说明正在主动更改 IP 与共享以便自行测试，因此停止依赖当前网络的验收，未改回用户网络。该次诊断不算最终发布包联网成功，先前真机隧道三轮通过的结果仍单独保留。共享修改后实际页面即时刷新仍需用户本次手动测试确认。

完整包：`artifacts/publish/QuantumHarness/0.3.20/win-x64/QuantumHarness.exe`；归档：`artifacts/releases/QuantumHarness-0.3.20-win-x64.zip`。密码未保存到源码、日志或文档，未自动重放任务，未停止用户已有 APP。
