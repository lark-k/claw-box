# ADR 0010：随身板子的简化连接

状态：采纳。日期：2026-10-14。取代 ADR 0009 中首次必须手填可信指纹的界面要求。

用户首次填写设备地址、SSH 端口和 root 密码，明确确认当前连接的是自己的板子后，软件自动获取 Ed25519 主机公钥，并以该身份限定本次 SSH/SFTP 会话。首次获取不具备供应商身份认证，依赖用户连接正确的设备和可信直连环境。已保存身份和本窗口已确认身份不自动替换；日常 OpenSSH 仍使用 StrictHostKeyChecking=yes。

无密码探测在主机密钥事件中拒绝连接，仅取公钥，不发送用户密码、不登录、不改板子。配置检查期间仅在窗口记忆初次身份；成功配置或中途已保存的 known_hosts 提供持久身份。用户重新尝试不会在同一窗口重新接受变更的公钥。

同一块板子改变地址时，验证新地址的公钥与已存身份相同后，仅更新本机 profile/known_hosts，保留原隧道密钥、执行器凭据和网关凭据；不更新板子插件，不重放任务。明确区分新电脑首次绑定与同一用户换网络重连。当前仍为单用户工作连接，换电脑需先关闭旧软件并显式绑定。

连接窗口不选择、修改或展示工作目录。工作区继续由首页选择，连接使用其当前值。已有配对文件导入放在高级入口。

网络诊断读取有线网卡 IPv4 和掩码，不能单凭网段判断电缆或联网状态。IPv4 设置按钮打开软件内专用编辑窗口。用户选中具体有线网卡并保存时，通过固定系统 netsh 命令修改该网卡 IPv4，申请 UAC，不改变 Wi-Fi、DNS 或系统共享设置；静态配置不设置默认网关。恢复 DHCP 也须显式点击。它不作为智能体工具暴露。网络共享使用所选网卡的 Windows 属性窗口，可选功能使用 Windows Settings URI。

参考：[netsh IPv4](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface)、[Windows Settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)、[Shell 网络连接文件夹](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/ne-shldisp-shellspecialfolderconstants)。

输入完成并失去焦点、确认设备且端口可达后，自动只读检查；输入改变取消旧请求并按版本拒绝过期结果。密码摘要只用于窗口内防重复，相同输入不定时重试失败登录。修改板子和网卡仍须点击，自动检查不重放操作。网络故障处理后允许一次只读复查。

板子 IP 检查必须同时验证当前地址和持久 NetworkManager/systemd-networkd 配置。未支持的网络后端、DHCP 或临时地址不能宣称固定。模型网络仅验证已配置服务端口，实际推理由聊天验证；已知不可达时在安装前停止。连接组件安装入口仅在缺少 Windows OpenSSH 客户端时显示。

0.3.14 的显式绑定优先复用按内容摘要核验的现有插件加载目录。相同配置和凭据不重写、不生成备份或重做安装验证。换电脑只更新 worker 绑定和受限公钥；worker 内容/加载设置变化需网关重载，单纯公钥扩展或凭据导出不重载。启动持久配对自动尝试连接，失败才引导。APP 通过匿名、不可继承的 kill-on-close Job 管理自己启动的后台进程，不接管外部执行器，避免闪退后的残留端口。
