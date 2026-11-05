# Quantum Harness 品牌覆盖

`python -B src/Quantum.Brand/apply.py` 在固定上游提交上应用 Quantum Harness 品牌。名称覆盖面为桌面菜单、关于、托盘、窗口与客户端本地化产品文案；图标复用本仓库 `ClawBox.Desktop/Assets` 中与 main 相同的 PNG/ICO。`DSH_CLIENT_BUILD_PROFILE=quantum` 与 `DSH_CLIENT_TITLE=Quantum Harness` 配合完整构建。

开发机器需要 `python -m pip install -r src/Quantum.Brand/requirements.txt`。侧栏使用 28px 图标与单行字标，首页使用较大的同款组合；品牌区域不显示版本号、提交哈希、dirty 或预览徽标。版本信息继续由“关于”提供。图标经 `/favicon.svg` 由应用自身提供，避免未发布的 CSS 图片路径导致空白标记。

技术包名、协议标识、模型提供商名称、上游许可证及第三方归属不改名。工具、执行权限、会话和附件实现不在覆盖范围。修改后的子模块工作树不应直接提交成无法追踪的 gitlink；本目录配方才是品牌改动的维护来源。再次执行为幂等操作，未知本地修改会停止应用。生成的 `.dsh-build/quantum-brand.json` 记录图标及覆盖文件哈希。

盒子入口实现由本目录的 `QuantumBox.tsx`、`quantum-box-apply.ts`、`box-settings.ts`、`quantum-companion.ts` 和本地化文件生成到固定上游。Quantum 注册分支替换官方账号客户端组合，并通过桌面专用进程提供盒子连接。连接架构和开放蓝牙加入的安全边界见 ADR 0024、0025。

Windows 任务栏应用 ID 为 `com.quantum.harness.next`，与旧程序分组。安装器使用标准 NSIS 向导，避开上游定制安装框架的 Visual Studio C++ 编译依赖；标准向导允许用户选择安装目录。该选择不替换或精简应用运行时。未部署 Quantum 更新服务时，未签名预览包不绑定上游强制更新策略，不发布到上游更新源；正式发行仍需独立配置 Quantum 更新服务和签名。

`runtime-path.ts` 在 Electron Node 模式下解析 Windows 的物理归档位置，再通过原 Host 主入口启动，避免应用目录重定向造成共享框架重复加载。`quantum-nsis.mjs` 只修正标准卸载模板的长路径文件操作，保留升级时的原子移走、失败恢复及用户数据策略；不绕过插件或运行时验证。深目录覆盖安装应由 `Test-HarnessNextInstaller.ps1` 实测。

`scripts/release/Publish-HarnessNext.ps1` 构建未签名 x64 安装包，产物复制到 `artifacts/releases/harness-next`。安装包不携带开发用户的模型密钥、网关令牌或聊天记录；新用户需要在模型设置中配置服务，后续由盒子接入流程替代开发配置。测试机已有的模型与会话仍在仓库外的私有目录中。
