/** Quantum connection copy, owned by the box feature. */
export const en = {
  title: 'Connect box', menu: 'More', settings: 'Settings', close: 'Close',
  heading: 'Your Quantum box', status: 'Not connected',
  description: 'Connect your box to use the model service provided by Quantum Harness.',
  pending: 'Box connection will be enabled after device setup. Saving these details does not connect a device or change your current model.',
  host: 'Box address', hostHint: 'Enter an IPv4 address or device hostname, such as quantum-box.local.',
  hostPlaceholder: 'quantum-box.local', port: 'SSH port',
  save: 'Save configuration', saving: 'Saving…', saved: 'Configuration saved · not connected',
  invalidHost: 'Enter a valid IPv4 address or hostname without a protocol, path or port.',
  invalidPort: 'Enter a whole number between 1 and 65535.',
  failed: 'Could not save the configuration. Try again.',
  unavailable: 'Connection settings are unavailable. Reconnect the app and try again.',
} as const
export type BoxKey = keyof typeof en
export const zh: Record<BoxKey, string> = {
  title: '连接盒子', menu: '更多', settings: '设置', close: '关闭',
  heading: '你的 Quantum 盒子', status: '尚未连接',
  description: '连接盒子，使用 Quantum Harness 提供的模型服务',
  pending: '盒子连接将在设备部署后启用。当前可先保存连接信息，不会建立连接或更改正在使用的模型',
  host: '盒子地址', hostHint: '填写 IPv4 地址或设备主机名，例如 quantum-box.local',
  hostPlaceholder: 'quantum-box.local', port: 'SSH 端口',
  save: '保存配置', saving: '保存中…', saved: '配置已保存 · 尚未连接',
  invalidHost: '请输入有效的 IPv4 地址或主机名，不含协议、路径或端口',
  invalidPort: '请输入 1 到 65535 之间的整数',
  failed: '配置保存失败，请重试',
  unavailable: '连接设置暂不可用，请重新连接应用后重试',
}
