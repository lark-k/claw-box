# 第六阶段本地验证记录

日期：2026-10-13。环境：Windows x64、PowerShell 7、.NET SDK 10.0.401。

## 结果

| 检查 | 结果 |
|---|---|
| 锁定依赖还原 | 通过 |
| Release 编译 | 通过，0 警告、0 错误 |
| 自动化测试 | 17 项通过，0 失败 |
| 格式检查 | 通过 |
| Windows x64 自包含发布 | 通过 |
| EXE/核心 DLL 的 PE 头与架构 | 通过 |
| 运行时 JSON 和依赖图 | 通过 |
| 文件 SHA-256 清单 | 通过 |
| 临时本地配置、私密 JSON 的发布排除 | 通过 |
| 发布后 EXE 的实际 HTTP 文件与 Word 操作 | 五个工具全部通过 |

发布包位于 `artifacts/publish/win-x64/`，构建日志与 TRX 位于被 Git 忽略的 `artifacts/`。CI 已配置同类检查，远程 CI 尚未运行。

## 可复现命令

```powershell
./scripts/ci/Verify.ps1
./scripts/ci/Test-Publish.ps1 -Runtime win-x64
./scripts/release/Test-Package.ps1 -Path ./artifacts/publish/win-x64 -Runtime win-x64 -RequireManifest
```

## 发布修复

之前中断的输出中发现 EXE、runtimeconfig 及部分构建缓存为全零内容。发布改用独立构建与临时输出目录；完成格式、配置和哈希验证后才替换最终目录。先前输出保留在被忽略的 previous 目录中以便排查。

项目明确禁止将环境特定 appsettings、私密 JSON 和 dotenv 文件复制到发布包。测试使用不含密钥的临时哨兵配置验证实际 SDK 行为，测试完成后移除哨兵配置。

## 范围

当前验证限于 Windows x64 本地执行器。win-arm64 已预留发布参数，未执行 ARM64 运行测试。盒子连接、桌面审批 UI、签名安装包与长期稳定性测试属于后续阶段。
