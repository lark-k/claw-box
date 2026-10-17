# 无线服务实机验证（2026-10-17）

## 范围和部署

在 RK3568 DG NAS LITE / Debian 12 / Linux 6.1.57-rk35xx-hiasia 上安装 RTL8723DU DKMS 模块、BlueZ 5.66、Avahi 和板子配网服务。源码、固定部署脚本、协议、ADR、测试均纳入仓库；第三方驱动源码与编译产物仅留在本机缓存和板子系统路径，不纳入 Git。

保留当前有线配置、SSH 主机身份及两份受限专用配对密钥。没有改动桌面 UI、OpenClaw 配置、模型密钥、worker/admin 凭据或执行器网络边界。配网服务以系统服务启动，不注册智能体工具。用户提供的测试 Wi-Fi 密码仅用于请求和板子 NetworkManager 私有配置，不写进源码、本文或命令行参数。

## 已验证

- USB 0bda:d723 的 Wi-Fi 接口成功绑定 8723du，生成 wlan0；Bluetooth hci0 保留工作。
- 头文件原有 x86-64 fixdep/modpost 在板子上重建为 aarch64；DKMS 正常构建、安装，重复执行固定驱动安装器成功。
- Windows 真实蓝牙扫描发现配网服务，连接 GATT，读取公开身份及 enrollment=false；不要求系统蓝牙 PIN。
- 使用已有专用私钥签名握手，核验已有 SSH 主机指纹；通过加密协议完成 Wi-Fi 扫描、配置、DHCP 地址获取及后续状态查询。
- 测试无线地址为 192.168.4.12。Windows 使用严格核验的主机密钥及原专用密钥，在该无线 IP 上 SSH 认证成功；路由返回电脑流量经 wlan0。
- 绑定 wlan0 的 HTTP 请求返回 200，确认无线链路能独立访问外网；有线接口一直保留。
- 使用刻意错误的测试口令连接：收到加密 wifi_connection_failed，临时配置被删除，原来的可用 Wi-Fi 自动恢复；没有新增持久错误密码配置。
- 主动断开 wlan0、重启 Bluetooth 服务后，原客户端仍可发现设备、通过原密钥认证、经 BLE 重新配置并恢复联网。服务不因无线不可用开放首次绑定。
- 陌生密钥通过真实 BLE 请求 resume 返回 not_owner，尝试 claim 返回 already_bound。未修改原有配对。
- Windows 只使用无线网卡查询 Avahi，发现 _clawbox._tcp、无线地址及 SSH 22 端口。
- 蓝牙和无线同时工作；关闭驱动 IPS/LPS/USB autosuspend，最终验证期间配网服务 NRestarts=0。

## 修复的实机问题

GATT 应用对象补齐显式 object path，并设置广播可发现标记。客户端按协商 MTU 传输，保留最小 MTU 的短片兼容。

复现了 dbus-python private SystemBus 的默认 exit-on-disconnect 会在正常 close 时直接使进程以 1 退出。NetworkManager 私有连接改用 NULL_MAIN_LOOP，明确 set_exit_on_disconnect(False)，与 GATT 主循环隔离。修复后多次扫描、状态查询、成功配网和失败回退不再退出。该行为加入回归测试。

systemd 使用 notify，GATT 和广播注册成功才 ready；Bluetooth/NetworkManager 生命周期通过 PartOf 关联，停止时正常退出。安装器验证服务 ready，保留并在失败时尝试恢复前一版本。

## 自动测试

`scripts/ci/Verify.ps1` 通过：243 项 .NET 测试通过、3 项原有环境相关测试跳过；11 项 Node 插件测试通过；62 项 Python 板子测试通过；构建零警告/错误、桌面状态恢复和格式检查通过。

新增 23 项 Python 测试在 Windows（cryptography 50.0.2）及真实 Debian 板子（发行版 cryptography）均通过，覆盖首次登记、原有配对导入、原有 SSH 密钥保留、撤销、不重新开放绑定、签名与主机身份、加密篡改、重放、跨 peer、断线和过期、密码不泄漏、无任意命令、MTU 分片、中文 SSID 字节保真、参数校验、内存配置、成功持久化及失败回退、D-Bus 关闭行为。

## 验证边界

本次只有一个用户授权的实际测试 SSID。已验证无线断开后通过 BLE 配置恢复，以及错误口令回退；两个不同路由器之间的真实切换仍需第二个测试网络。未清空实际设备已有配对来测试首次抢占，首次登记逻辑通过 Linux 文件系统和协议测试验证。

桌面无线向导未实施，当前验证客户端是工程命令行入口。网络协议不支持企业 Wi-Fi 认证和网页门户自动登录。

## 系统重启验证

完成整机重启，确认 boot ID 已变化。开机后无需人工操作，8723du 自动装载且 IPS/LPS 参数为 0，蓝牙、NetworkManager、Avahi 和 clawbox-network 均为 active；配网服务约在开机 15 秒完成 GATT/广播注册，NRestarts=0。有线地址保持 192.168.137.2，无线自动连回已保存网络并取得 192.168.4.12。

Windows 再次通过 BLE 建立加密会话，以原专用密钥认证并读取 connected=true 和无线地址；对该无线地址严格验证原 SSH 主机密钥后，使用原专用私钥认证成功。没有重新配对或重新输入 Wi-Fi 密码。仓库源码/文档中检查不到实际测试 Wi-Fi 密码。
