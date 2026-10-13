# ADR 0001：Windows 执行器技术与授权边界

状态：采纳。日期：2026-10-13。

## 背景

RK3568 已部署 OpenClaw 并能调用远程模型。Windows 端需要执行文件和 Word 操作，后续与桌面 APP 同包安装，代码须适合长期维护。

## 决策

采用 .NET 10 LTS，SDK 和包版本集中固定。使用 ASP.NET Core 本地 HTTP 作为首版接口，拆分协议库与宿主。使用 Open XML SDK 操作未打开的 DOCX 文件，不依赖 Word COM、宏或交互窗口。

用户审批与工具调用采用不同凭据；审批绑定单个请求的不可变参数。SQLite 保留任务和审计；恢复采用标记中断，避免自动重放变更。文件工具通过工作目录策略限制访问。

## 影响

后台 DOCX 读写可在没有 Office 的测试环境验证。首版仅支持正文、表格中的普通文本，不承诺页眉页脚、复杂域、修订模式或自动版面重排。替换文字继承匹配起点的文字格式，其他格式片段保留。

打开中的 Word 窗口操作、实时审批 UI、盒子配对和远程连接是独立扩展。新增这些能力须补充 ADR 与验收用例，不能直接开放任意 shell 或 COM 方法给模型。

参考：[.NET 安装](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、[Open XML SDK](https://learn.microsoft.com/en-us/office/open-xml/open-xml-sdk)。
