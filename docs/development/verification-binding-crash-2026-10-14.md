# 绑定并连接闪退修复（2026-10-14）

版本：0.3.13。针对用户点击“绑定并连接”后 APP 直接退出的问题。

## 原因和修复

Windows Application 日志中 .NET Runtime 记录的两次真实崩溃（22:45:35、22:47:00）均为 `ArgumentOutOfRangeException(mode)`，调用链是 `SftpFileAttributes.SetPermissions → SftpClient.ChangePermissions → SshBoardSetupTransport.Configure → BoardSetup.Provision → ConnectionSetupWindow.Execute`。异常越过 async 点击事件，终止 WPF 进程。

上传代码误把 0600 的位掩码十进制值 384 传给 SSH.NET。当前 2026.0.0 接口要求按八进制数字写出的 chmod 模式，因此应传 600。两个上传路径统一使用该值，文件仍仅允许所有者读写。依据：[官方接口文档](https://sshnet.github.io/SSH.NET/api/Renci.SshNet.Sftp.SftpFileAttributes.html#Renci_SshNet_Sftp_SftpFileAttributes_SetPermissions_System_Int16_)、[当前版本源码](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/Sftp/SftpFileAttributes.cs)。Context7 未返回 SSH.NET 的匹配库，因此核对官方材料与安装包 XML。

向导自动检查和显式配置的异常边界增加 ArgumentException 处理；操作失败显示安全提示，恢复按钮和表单，不输出原异常中的密码、凭据或远端任意内容，不自动重放失败配置。

## 回归证据

新增两项测试先在修复前失败，再在修复后通过：

- 使用真实 SSH.NET SftpFileAttributes 复现 384 抛错，验证生产代码所用模式能够解析，所有者读写正确，组/其他用户/执行及特殊权限全部关闭，并保留普通文件类型。
- 在 WPF Dispatcher 中模拟后台配置抛出同一 ArgumentOutOfRangeException，验证异常不会逃逸、失败提示不含模拟敏感消息、表单和按钮恢复，以及后续显式重试仅执行一次。测试不接触真实板子或用户配对数据。

`scripts/ci/Test-DesktopPublish.ps1 -SideBySide` 通过完整包、清单、哈希和自包含原生 EXE 渲染。截图目录：`artifacts/desktop-qa/publish-46e9b8ffcfe54e9188223d4df07253dd/`。

修复包：`artifacts/releases/QuantumHarness-0.3.13-win-x64.zip`；解压完整包后运行 QuantumHarness.exe。既有密钥、凭据和工作区保留。保持用户正在运行的旧 APP，不在测试中终止它，也不对真实板子重新绑定、重启网关或重放部署。真实密码绑定与对话需用户在新版显式重试，不能用本次回归代替该验收。

最终 `scripts/ci/Verify.ps1` 通过：桌面 100 项、执行器 63 项、插件 11 项、板子 Python 25 项，以及状态修复和格式检查。执行器既有 AppContainer 环境测试及桌面 opt-in 真实身份测试各跳过 1 项，未计为通过。构建零警告、零错误。
