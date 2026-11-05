# Quantum Harness 新方案开发

本分支采用 [ADR 0020](../architecture/adr/0020-official-harness-and-model-box.md)。保留官方工具和权限实现，提供 Quantum 品牌的 Windows x64 未签名测试安装包、隔离预览入口和模型网关。盒子认证与转发已完成部分实机验证，安装版端到端验收仍在进行；正式公开发行还需代码签名。

## 获取源码

```powershell
git submodule update --init --recursive
```

子模块固定 `ce7073d51254c26cd63b22a60835b8f948deb447`。不运行 `git submodule update --remote`。源码内的 SDK、原生依赖和构建要求以固定版本的说明为准；上游要求 Node.js >=24.9.0、pnpm 11.28.5。旧项目使用的 Node.js 22 不足以构建新方案。

## 模型网关

2026-11-05 实机阶段：网关已安装到当前 RK3568，`quantum-model-gateway` 仅监听盒子的 `127.0.0.1:18792`。板端私有配置采用 `upstreamInterfaces` 优先尝试 wlan0、再尝试 eth0；此处只在 TCP 建连阶段切换出口，不重发已发送的模型请求。板上旧蓝牙服务已更新为支持独立密钥的 join 协议，原有配对密钥保留。网关可签发并分别撤销客户端令牌；盒子侧源码备份位于 `/var/backups/quantum-harness/`。已在盒子验证真实模型工具调用和两份独立令牌的使用及撤销。新桌面界面及双电脑端到端连接尚未完成，因此现有安装包不具备该功能。

后续诊断中已将该盒子的 BLE 广播间隔从 1280 ms 调整到 100–150 ms，Windows 已能发现设备。三次 Windows 原生搜索、原配对认证，以及同一电脑模拟的两个 BLE 身份配对重连和 Windows 原生模型隧道测试均通过。新桌面连接页已接入有线授权、无线发现和配对、自动模型配置；仍需完成安装版图形界面和两台物理电脑的端到端验收。详细结果见 [实机验证记录](verification-quantum-box-2026-11-05.md)。

在运行网关的设备上，将私有 JSON 配置保存到仓库外，仅授予运行用户读取权限：

```json
{
  "upstream": "https://your-model-server.example/v1",
  "upstreamToken": "replace-with-server-model-credential",
  "token": "replace-with-different-paired-client-credential",
  "model": "your-model-id",
  "allowHttp": false
}
```

```powershell
python -B src/Quantum.ModelGateway/gateway.py --config '<私有配置绝对路径>' --port 18792
```

网关仅监听 127.0.0.1。部署到盒子后通过核验主机身份的 SSH 本地转发访问；需为新端口配置专门的转发授权，不能沿用旧密钥配置并假定已经可用。本地开发可直接运行网关验证真实服务器；此方式没有经过盒子。

```powershell
python -B scripts/dev/Test-ModelGateway.py --config '<私有配置绝对路径>'
python -B -m unittest discover -s tests/Quantum.ModelGateway.Tests -p 'test_*.py'
```

真实 smoke 只调用无副作用的加法测试工具；不读取用户工作区、不调用 shell、不打印凭据。模型请求需要支持 OpenAI Chat Completions 流式响应和工具调用。图片字节可以透传，但模型视觉能力另行验收。

## 启动官方界面

### 本机双击预览

本机已准备好桌面依赖和配套运行时后，可以运行 `scripts/dev/New-HarnessNextShortcut.ps1 -DesktopShortcut` 创建桌面与 `artifacts/preview` 下的 **Quantum Harness Next** 快捷方式。该入口使用仓库外的私有模型配置，自动启动一个动态回环端口的本机测试网关，然后运行官方 Electron 应用；本轮不依赖实体盒子。

窗口名称为 **Quantum Harness**。快捷方式是开发预览入口，安装包位于 `artifacts/releases/harness-next`。源码预览依赖当前工作区、Python 和已准备的 Node/npm 缓存，移动工作区后需重新生成；安装版自带运行时。开发入口不会替换旧版已安装软件。

关闭窗口可能按上游行为隐藏到托盘。完全退出请使用应用或托盘的退出入口；正常退出后启动器清理自己创建的网关进程。重复双击不启动第二个网关。诊断日志位于 `%LOCALAPPDATA%/QuantumHarnessNext/preview.log`，失败提示位于同目录的 `preview-error.log`；日志可能包含本机运行时认证 URL，分享前请脱敏。`Open-HarnessNext.ps1 -CheckOnly` 只检查预览条件。

### 源码开发入口

设置进程环境变量 QUANTUM_GATEWAY_TOKEN 为配对网关凭据（不使用服务器模型凭据），运行下列命令。脚本通过 npx 使用固定 Node.js 24.9.0 与 pnpm 11.28.5，不替换系统 Node.js；首次下载需要网络。子模块的 Git 配置会按上游安装钩子的要求启用工作区配置，仅影响该子模块。

```powershell
./scripts/dev/Start-HarnessNext.ps1 -Model '<模型 ID>' -Prepare
```

默认启动上游 Web UI，先验证完整智能体和工具链。`-Surface Desktop -Prepare` 选择上游 Electron 开发入口并构建桌面资源。`-ConfigureOnly` 只生成隔离模型配置。`-ImageInput` 仅在实际模型确认支持视觉时声明图片能力。已有 profile 配置保持不覆盖；调整模型与端口需编辑生成的配置。

`-DisableThinking` 为支持 chat_template_kwargs 的 Qwen/vLLM 端点在首次配置时声明 enable_thinking=false，与本轮真实模型 smoke 一致；不是所有模型都支持该参数。默认不发送此覆盖。

脚本使用 `%LOCALAPPDATA%/QuantumHarnessNext/harness` 及 `electron` 存储预览状态。UI 使用 Quantum 品牌，保留通用模型配置；首次预览配置将默认模型设为 Quantum Box。Web 默认使用 18893 端口，可用 `-WebPort` 调整；`-NoOpen` 禁止自动打开浏览器。这里不自动迁移旧聊天或更改已安装旧版的配置。

## 维护

暂停开发时可删除 `.desktop-build` 下的重复发布目录、packed/package-set/dsh 暂存目录、pnpm-store 和 downloads，以及独立 installer-smoke 测试目录。这些由发布脚本重新生成；后续首次重新打包可能需要下载依赖。保留源码依赖、编译输出、Electron/primary-runtime、仓库外的冻结预览程序和私有配置，以及 `artifacts/releases/harness-next` 中已交付的安装包。

上游子模块固定原始提交；`src/Quantum.Brand` 配方与其源文件是品牌及连接页改动的提交来源。运行配方后，子模块工作区显示修改属于预期；不要把该状态提交为另一个未经发布的子模块版本。排查额外改动时使用 `git -C src/Quantum.Harness.Upstream status`，配方会拒绝覆盖不认识的文件修改。

Quantum 代码在子模块外维护；上游源码改动必须作为独立补丁或提交记录。上游仓库及其依赖许可保持随源码提供，最终发行时按实际打包内容核对。根目录 `scripts/ci/Verify.ps1` 包含旧版回归与新网关测试，不能替代上游构建和实体盒子验收。

早期验证结果见 [2026-11-03 验证记录](verification-harness-next-2026-11-03.md)，实体盒子测试见 [2026-11-05 记录](verification-quantum-box-2026-11-05.md)。

功能以固定版本官方组合包为基准：不另建精简 UI，也不删除或替换官方工具、会话、工作区、附件、插件、技能或权限实现。实验功能保持上游默认启用状态；网络搜索、其他模型和第三方插件的外部服务依赖不会因为更换模型接口自动具备。新增兼容问题必须记录并修复，不能以隐藏入口作为完成方式。

## Quantum 品牌安装包

### 盒子入口（2026-11-03）

Quantum 构建已将侧栏“更多 → 登录”替换为“连接盒子”，设置中同样提供连接页；不再注册官方账号、余额、充值和官网登录引导。新安装也跳过官方原生登录欢迎页。原有通用模型提供商配置保留。

当前桌面入口提供有线连接引导和无线 BLE 配网，自动读取电脑当前 Wi-Fi；连接后通过本机受限隧道配置盒子模型。新电脑无需配对密钥即可加入，蓝牙范围内任何设备也可自行加入并使用模型，详见 ADR 0025。连接配置和电脑专用令牌存于用户私有目录。安装版启动及连接页面已检查；两台物理电脑和目标 Wi-Fi 的完整配网仍需实测。

需要检查最新源码界面时使用 `scripts/dev/Open-HarnessNext.ps1 -Source`。该入口仍与打包共用互斥锁，运行期间不可重建资源。冻结安装包必须重新构建和通过插件、原生运行时与 Office 验证后才能作为本次变更的发行包。

执行 `scripts/release/Publish-HarnessNext.ps1` 生成 x64 未签名 NSIS 安装包，复制到 `artifacts/releases/harness-next`。当前应用版本沿用固定运行时的 0.2.2-alpha.2，品牌为 Quantum Harness；该版本号不是旧 WPF 版本。安装器允许选择目录，采用标准向导，不需要本机 Visual Studio C++ 工具链。正式发行前仍需自己的签名证书及更新服务。

### 覆盖升级

后续新方案保持 `com.quantum.harness.next` 应用标识、Quantum Harness 产品名和用户数据路径不变；新版安装包沿用安装身份并替换程序文件。配置、会话和工作区位于程序目录之外，不随程序覆盖。发布时须递增版本；固定上游的测试迭代可使用 `Publish-HarnessNext.ps1 -BuildVersion 0.2.2-alpha.2.20261103.2`，由上游版本校验器验证，不能仅给 EXE 改名冒充新版。

当前提供手动下载安装包的覆盖安装，不提供应用内自动检查或下载更新。`scripts/ci/Test-HarnessNextInstaller.ps1 -Installer <EXE>` 验证独立安装、首次启动、同版本覆盖重装、配置/工作区保留以及卸载。它不代替跨版本数据迁移验收；未来修改存储格式时，须针对实际旧版与新版另做升级测试。

品牌覆盖见 `src/Quantum.Brand/README.md`。构建与预览共享互斥锁，防止开发资源在运行中被重建导致白屏。完成打包后预览入口优先运行冻结的 win-unpacked 程序，避免上游 HMR 对产品验收的干扰。开发预览才开启本机调试端口，安装器创建的正常启动入口不带调试参数。

官方模型设置会将 JSON 配置改写为 YAML；预览启动器现使用 js-yaml 读取并保留配置，只更新自己拥有的网关地址。测试覆盖 JSON、YAML 与异常配置不被覆盖。安装包不包含 `%LOCALAPPDATA%/QuantumHarnessNext/model.secrets.json`；向新用户分发前需完成模型或后续盒子的独立配置。
