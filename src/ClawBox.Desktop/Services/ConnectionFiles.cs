namespace ClawBox.Desktop.Services;

public static class ConnectionFiles
{
    public static string Read(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{description}路径无效。请在“连接与设置”中选择配置文件。");
        try { return File.ReadAllText(path); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException($"未找到{description}。请在“连接与设置”中检查文件位置；如果配置由打包应用中的终端生成，请按运行指南迁移到普通 Windows 用户目录。", error);
        }
    }
}
