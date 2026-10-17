# 板子无线服务安装与维护

App 0.3.24 已提供 Windows 原生无线向导，设置分别提供“有线连接”“无线连接”“更换板子／重新绑定”。Wi-Fi 配置及改网经 BLE 进行，已绑定电脑不需要旧网络、网线、二维码或配对码。完整接口见 `docs/api/board-network-v1.md`。

## 板子准备

当前验证平台为 RK3568 DG NAS LITE、Debian 12、`6.1.57-rk35xx-hiasia`、RTL8723DU（USB `0bda:d723`）。当前 vendor 内核没有 8723DU USB Wi-Fi 模块，但蓝牙驱动和固件已经存在。安装 BlueZ、Avahi 及 Python D-Bus、GLib、cryptography 运行库；使用发行版可信签名软件源，不运行全系统升级、不改变有线 IP。

```sh
apt-get update
apt-get install --no-install-recommends bluez avahi-daemon python3-dbus python3-gi python3-cryptography
```

将仓库源码放到板子私有目录。驱动第三方源码和编译结果不放入仓库。当前固定源为 [lwfinger/rtl8723du](https://github.com/lwfinger/rtl8723du)，revision `ae03b0861351f72808405ddda80e8aab105bcfce`。源码归档 URL：`https://codeload.github.com/lwfinger/rtl8723du/tar.gz/refs/heads/master`，必须匹配安装器内 SHA256 和归档 commit；分支更新时安装器拒绝使用不匹配的内容。

上游已将这个 vendor 驱动标记 obsolete，并推荐 rtw88。本次为已有定制 6.1 镜像采用固定 revision，避免替换内核已有 rtw88 模块；这不是所有新内核的通用安装方案。换内核时必须重新验证驱动或采用包含原生 rtw88 USB 支持的镜像。

```sh
python3 -B scripts/board/install-rtl8723du.py /path/to/verified-driver-source.tar.gz
python3 -B scripts/board/install-network.py
```

该内核头文件中 fixdep/modpost 是 x86-64 可执行文件。驱动安装器先保存原件至 `/root/.local/share/clawbox/header-tools-backup/`，仅重建这两个 host 工具为 aarch64，不编译或更换内核。DKMS 构建参数显式提供 TopDIR。驱动装入 updates/dkms，登记自动重建；模块自动加载和省电关闭分别记录在 `/etc/modules-load.d/clawbox-wifi.conf` 和 `/etc/modprobe.d/clawbox-wifi.conf`。关闭 IPS/LPS 以提高本板 Wi-Fi/蓝牙并行运行稳定性。

服务源在 `/opt/clawbox-network`，上一个安装版本在 `.previous` 目录。安装器使用 systemd notify，只有 GATT 和广播注册成功才判定启动成功；失败尝试恢复旧源码、unit 和 Avahi 配置。它不修改 OpenClaw、执行器、已有主机密钥或已有配对内容，仅记录已绑定标记。

```sh
systemctl status clawbox-network bluetooth NetworkManager avahi-daemon
systemctl is-enabled clawbox-network bluetooth avahi-daemon
bluetoothctl show
nmcli device status
ip -br address show wlan0
```

`clawbox-network` 是 root 维护服务，但限制到 Unix sockets、只读系统目录及最小写目录，不监听 TCP，不向客户端提供 shell。NetworkManager 调用使用与 GATT 主循环隔离的 private D-Bus 连接，明确关闭 exit-on-disconnect，避免正常关闭连接导致整个进程退出。

## 工程客户端

Windows 开发机安装测试依赖及 `bleak==3.0.2`。Python BLE 客户端仅用于开发验收，最终用户通过桌面向导使用，无需安装 Python。`--key` 是现有专用私钥路径，不把私钥上传板子；`--fingerprint` 来自已有 known_hosts，不能凭陌生广播替换。

```powershell
python -m pip install -r tests/ClawBox.Board.Tests/requirements.txt
python -m pip install bleak==3.0.2
python scripts/board/provision-client.py discover
python scripts/board/provision-client.py status --address <BLE地址> --key <专用私钥路径> --fingerprint <已知指纹>
python scripts/board/provision-client.py configure --address <BLE地址> --key <专用私钥路径> --fingerprint <已知指纹> --ssid <无线网络名>
```

密码由交互输入，不支持 `--password`，不应写进脚本、日志或命令历史。新设备须生成客户端专用 Ed25519 密钥、显式选择设备后使用 `--claim`，已有绑定会拒绝该请求。已登记设备断网仍广播，但陌生密钥不能扫描网络或改网。服务或客户端重启会建立新加密会话，绝不自动重发未确认的 configure。

## 回退与排查

停止本功能：`systemctl disable --now clawbox-network`。该操作保留已有 Wi-Fi 配置和配对。需要回退服务时，停止服务后恢复 `/opt/clawbox-network.previous` 及对应旧 unit，再 daemon-reload、启动。Avahi 服务定义在 `/etc/avahi/services/clawbox.service`，只发布 SSH 22。

驱动回退需保留网线：删除本功能的模块自动加载/参数配置，卸载 8723du 后以 `dkms remove -m rtl8723du -v 0.1 --all` 移除模块；不会触及 btusb 和有线驱动。构建工具原件可按上述备份位置恢复。不要清理 authorized_keys、SSH 主机密钥或用户网络文件来“修复”连接。

Wi-Fi 失败时先看加密 status、NM 状态及日志；共享网卡、路由器隔离、DHCP 和门户登录属于网络条件。验证无线独立可用，应通过无线 IP 严格核验 SSH 主机密钥并用原专用密钥登录，而不是只看到 wlan0 有地址。桌面向导从 BLE 读取地址，核验既有主机身份后保存；蓝牙不可用时可输入同局域网无线地址。
