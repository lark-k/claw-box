# ADR 0008：固定项目命令与依赖安装

状态：采纳。日期：2026-10-14。

新增 project.command，参数为工作区项目目录、固定 operation 和最新源码快照摘要。支持 npm.install、npm.ci、build、test、git.status、git.diff；构建与测试复用无网络 AppContainer。Git 在无网络的独立源码与 Git 元数据副本中运行，关闭外部 diff、textconv、fsmonitor、hooks、子模块遍历与用户配置，仅展示源码快照内的文件。拒绝命令字符串、任意参数、任意可执行文件和板子 shell。

依赖安装仅运行受信任本机 Node/npm CLI，在独立 AppContainer 中授予 internetClient；不继承用户环境或凭据，不读取用户/项目 npmrc，固定 npm 官方 registry，禁用全部安装脚本。只接受 registry 依赖，拒绝 Git、file、URL、workspace 和链接来源。安装不执行项目代码。联网能力不提供给构建、测试或其他工具。

安装成功后核对源码快照仍然相同，再发布有界、无链接的 node_modules 和 package-lock.json。已有 node_modules 改名备份保留，发布失败恢复；未知中断不重放安装。锁文件以原件 SHA256 原子写入。命令回执包含 operation、exitCode、输出和真实成功状态；失败不终止智能体整个任务，由模型依据日志调整。仍使用原请求级审批，完全访问逐请求批准。

APP 只对连续 30 分钟无有效模型输出或工具进展进行取消；重复轮询、UI 计时与动画不续期。盒子整轮 timeoutSeconds=0，不再按整轮累计时间终止；单条命令的资源与执行期限继续保留。
