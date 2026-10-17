# 板子蓝牙配网协议 v1

本协议由桌面 App 0.3.24 的无线连接向导使用。板端实现位于 `src/ClawBox.Board.Network/clawbox_network/`，Windows 原生客户端为 `BluetoothNetwork.cs`、`WirelessProtocol.cs`，工程参考客户端为 `scripts/board/provision-client.py`。决策及首次无配对码信任边界见 ADR 0016、0017。此服务不属于智能体工具接口。

## 发现和传输

服务 UUID `4e170001-8f64-4a12-9d85-1c3a25dc4d01`。广播名 `ClawBox-<公开身份短摘要>`，只用于显示和选择，不能用于认证。BLE 地址不是持久设备身份。

| 特征 UUID 前缀（其余与服务 UUID 相同） | 属性 | 内容 |
|---|---|---|
| `4e170002` | read | ASCII JSON：version、SSH 主机指纹 identity、enrollment |
| `4e170003` | write | 请求流；每条规范 JSON 后接一个 LF；可按 ATT MTU-3 分片 |
| `4e170004` | read | 分片响应：大端 uint32 总字节数、uint32 本片偏移、内容 |

没有响应就绪时 read 返回两个 0。客户端逐片读取，校验总长、连续偏移，收齐后解析 JSON；同一设备一次只发一条请求。读片短于 MTU-1，避免 Windows 自动 Read Blob 与应用分片混淆；不使用缓存读取。最大请求及响应 8192 字节，最多八个 peer、会话有效期 180 秒；客户端握手可以重新开始，写操作不能自动重试。断开会丢弃未完成帧与会话，不重放已接收的配置。

## 加密与所有者认证

规范 JSON 为 Python `json.dumps(sort_keys=True,separators=(',', ':'),ensure_ascii=True)` 的 ASCII/UTF-8 字节。Base64 是 RFC 4648 标准带 padding 编码。SSH 公钥为 OpenSSH `ssh-ed25519 <base64>`，不带注释。哈希 SHA-256，签名 Ed25519，前缀为 ASCII `clawbox-network-v1` 加一个 NUL。

1. 客户端发 `hello`：`op=hello,version=1,mode=claim|resume,owner=<公钥>,nonce=<32随机字节Base64>,ephemeral=<32字节X25519公钥Base64>`。已有配对必须 resume；claim 仅用于用户明确选择的新设备。
2. 板子回复 `{transcript,signature}`。transcript 包含完整 hello、随机 session（32 个十六进制字符）、host_key、identity、nonce、ephemeral。signature 是主机私钥对 `前缀 + canonical(transcript)` 的签名。identity 为 SSH 标准 SHA256 指纹。客户端必须验证签名、回显 hello 和已知指纹；首次信任保存这份主机身份，不要求用户抄写指纹。
3. 双方 X25519 共享值经 HKDF-SHA256 派生 64 字节。salt 为 SHA256(canonical(transcript))，info 为前缀；前 32 字节为 client→server 密钥，后 32 字节为 server→client 密钥。两方向分别使用 AES-256-GCM。
4. 加密信封 `{session,seq,ciphertext}`。seq 从 0 严格递增，必须是整数；nonce 为 seq 的 12 字节大端表示。AAD 为 canonical({session,direction,seq})，direction 分别是 `client` 和 `server`。响应使用对应请求的 seq。认证失败、操作失败也消费该序号；响应丢失需要查询现状，禁止重发配网写请求。
5. seq=0 的明文为 `{op:authenticate,proof:<签名Base64>}`。proof 是所有者专用密钥对 `前缀 + SHA256(canonical(transcript))` 的签名。首次 claim 成功时登记受限 SSH 公钥；已有配对复用 authorized_keys 中现有受限 `clawbox-reverse-tunnel` 密钥。只知道公钥不能改网；每次业务操作重新检查密钥是否已被撤销。

已绑定设备不重新开放 claim。`/var/lib/clawbox-network/enrolled` 标记确保撤销所有密钥后也不会自动开放。没有远程匿名恢复出厂或解除绑定接口。worker/admin token 都不用于此认证；仅首次桌面初始化会在认证后的加密通道传送受限 worker token，admin token 不经过蓝牙。

## 加密业务操作

响应明文统一为 `{ok:true,result:{...}}` 或 `{ok:false,error:<稳定错误码>}`。解密前的协议错误只返回 `{error:<错误码>}`，客户端应终止该次交互。

| op | 请求 | 结果 |
|---|---|---|
| status | 无其他字段 | interface、state（NM DeviceState）、connected、addresses，连接后含 ssid、ssid_bytes |
| scan | 无其他字段 | networks，最多 24 个，含 ssid、ssid_bytes、strength、frequency、security、saved |
| configure | ssid、password，另可含 ssid_bytes、security、hidden | 成功后的 status |
| connect_saved | ssid，可含 ssid_bytes、security；不得含 password 或其他字段 | 使用板子已保存配置连接后的 status |
| desktop_setup | worker_token，严格 64 位十六进制 | gateway_token，仅通过加密会话返回 |

`desktop_setup` 仅在用户点击“连接并开始使用”且桌面配对尚未完成时调用。板端执行随镜像交付的固定插件和桌面初始化脚本，不接受命令、路径或上传源码；不会注册为智能体工具。请求结果未知时不自动重放。普通换网只需 status/scan/configure。

0.3.25 起，`saved` 表示本服务成功持久化且 SSID/安全类型匹配的 Wi-Fi。密码仍仅由板子 NetworkManager 保存，不回传电脑。`connect_saved` 必须由已认证所有者显式发起，只激活匹配的已有无线配置；当前已连接时直接返回状态，失败保留原密码并尝试恢复此前网络。找不到配置返回 `saved_network_missing`，可输入新密码走 configure。App 启动恢复仅调用只读 status 取回变化后的地址，严格核验原 SSH 身份后重连，不自动调用配网写操作。

Wi-Fi SSID 是字节串。扫描给出原始字节 Base64 的 ssid_bytes，显示名优先 UTF-8，兼容 GB18030，最后使用替换字符；选择扫描结果后必须原样回传 ssid_bytes，服务验证其与显示名一致，避免中文名二次编码。手填隐藏网络未提供原始字节时按 UTF-8 编码。SSID 长度 1–32 字节。当前支持 WPA/WPA2 PSK（security=wpa-psk）和显式选择的开放网络（open）；不支持企业认证和网页门户自动登录。密码不允许控制字符，PSK 为 8–63 字节口令或 64 个十六进制字符。

配置仅操作无线设备，保留以太网及其他 SSID。新配置先以 NM AddConnection2 IN_MEMORY=2 创建，DHCP/激活成功才 Save；失败删除临时配置、尝试恢复原无线连接。操作超时 45 秒，蓝牙仍可读取 pending 帧。持久凭据保存在 NM 的 root-only keyfile，不出现在命令行、协议明文响应或服务日志中。

错误码包括 already_bound、not_owner、invalid_signature、identity_mismatch、invalid_sequence、session_expired、invalid_ciphertext、invalid_ssid、invalid_password、unsupported_security、wifi_driver_missing、wifi_unmanaged、wifi_connection_failed、wifi_connection_timeout、network_busy、operation_failed。失败后可读取 status；不保证网络错误都能唯一归因为错误密码。

联网发现另由 Avahi 发布 `_clawbox._tcp`、SSH 端口 22、协议版本与公开设备摘要。未来客户端获取地址后仍须验证同一 SSH 主机身份，不能因发现记录就信任新设备。

接口依据：[BlueZ GATT](https://github.com/bluez/bluez/blob/master/doc/org.bluez.GattCharacteristic.rst)、[BlueZ GATT Manager](https://github.com/bluez/bluez/blob/master/doc/org.bluez.GattManager.rst)、[NetworkManager Settings](https://networkmanager.dev/docs/api/latest/gdbus-org.freedesktop.NetworkManager.Settings.html)。
