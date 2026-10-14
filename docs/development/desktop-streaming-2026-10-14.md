# Quantum Harness 0.3.8：流式空白与 Vue 生成中断

日期：2026-10-14。

## 定位

本机水果商城会话的阶段文字有连续前置换行。TurnTranscript 在仅收到换行时仍写入 Message.Text，FoldUnmarked 在纯空白情况下直接返回而不清理；界面仅判断空字符串，MarkdownText 把真实换行渲染为空行。下一句被归入阶段概要后正文清空，于是间隙又消失。

盒子 OpenClaw 2026.10.2 的三次失败记录为 Provider returned an incomplete or malformed tool call。对应 assistant usage.output 全部为 2048，模型配置 maxTokens 也是 2048。较长 ProductDetail.vue、Cart.vue 的写入参数在输出上限处被截断，尚未提交到 Windows 执行器。另一次 main.js 覆盖写入因缺少当前文件摘要被 file_conflict 正确拒绝；生成的 package.json 缺少代码引用的 vuex，Cart.vue 缺失，因此当时的工程也不完整。

## 修复

展示用 DisplayText 去除首尾空白，HasText 使纯空白消息不占布局；二者不写入历史，原始文字保留。工具边界清除纯空白缓冲区，空阶段行隐藏。保留内部段落、代码缩进、思考扫描和处理计时。

通过已配对的管理员网关配置接口，将现有 Qwen 模型 maxTokens 从 2048 改为 8192；随后读回确认 contextWindow=65536、reasoning=false 与其他配置保持原值。配置工具自动规范化 modelPolicy 字段。APP 明确发送 max_tokens=8192，生成提示补充长源码拆分、依赖与路由核对以及已有文件必须使用摘要的要求。兼容入口支持该参数，见 [OpenClaw 入口实现](https://github.com/openclaw/openclaw/blob/main/src/gateway/openai-http.ts)。

这是每次模型生成的预算；完整工具调用结束后可以继续下一次生成。超长单次参数仍有上限，不自动重新提交未知写入。

## 验证

`ResponseFeedbackTests` 增加连续纯空白分片及工具边界回归，保留正文内部换行并检查展示字段不持久化。原生渲染增加 stream-whitespace、stream-next-line、stream-empty-progress，检查思考指示器坐标不因空白分片改变，下一句没有前后空行，空阶段标记没有可见行。

已检查 `artifacts/desktop-qa/stream-gap-038/` 实际截图，空白与下一句布局均正常。运行 `scripts/ci/Verify.ps1` 和 `scripts/ci/Test-DesktopPublish.ps1 -SideBySide`，覆盖正式自包含 EXE。真实模型验证使用隔离测试会话；诊断凭据未写入仓库或报告。

完整检查为 113 项通过，1 项多语言隔离运行时测试按环境跳过。0.3.8 自包含 EXE 的全部视觉状态检查通过。

真实模型的客户端虚拟工具回归最终返回完整 Cart.vue JSON，源码 7083 字符，单次 output=2606 tokens、stopReason=toolUse，SSE 收到 DONE。该工具没有文件操作副作用；证明实际输出已经超过旧 2048 限制并完整闭合。准备过另一份 Windows 工具验证副本，但旧 APP 占用独占执行通道，副本路径被现有工作区策略拒绝，因此不将这次尝试计为 Windows 工具通过。保留用户正在运行的 APP 与其通道。

水果商城副本补齐 vuex 4 依赖和 Cart.vue，并核对购物车条目使用 id、数量更新接口接收绝对数量。npm install 使用 --ignore-scripts；Vite 生产构建通过。Playwright 实测添加苹果、数量 1→2→1、金额 8.50→17.00→8.50、删除商品和空状态通过。控制台仅有缺少 favicon 的 404，没有 Vue 运行错误。

验证后的缺失 Cart.vue 与 vuex 依赖补回 `D:/Desktop/test/fruit-shop`，更新 package.json 前核对当前 SHA256，原配置备份放在隔离 QA 目录；已有页面未替换。该工程安装依赖后重新生产构建通过。安装生成 package-lock.json 与 node_modules，构建生成 dist，均留在该工程而非仓库源码。
