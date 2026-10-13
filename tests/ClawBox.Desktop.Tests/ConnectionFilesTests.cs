using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class ConnectionFilesTests
{
    [Fact]
    public void Diagnostic_does_not_overwrite_an_existing_file_or_connect_before_rejecting_it()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "existing user content");
            Assert.Equal(2, ConnectionCheck.Run(file));
            Assert.Equal("existing user content", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Diagnostic_requires_an_explicit_absolute_report_path()
        => Assert.Equal(2, ConnectionCheck.Run("report.json"));

    [Fact]
    public async Task Missing_pairing_directory_fails_before_launch_with_actionable_message()
    {
        using RuntimeSession runtime = new();
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "connection.json");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Start(new DesktopSettings { ProfilePath = missing }, CancellationToken.None));
        Assert.Contains("未找到盒子配对文件", error.Message);
        Assert.Contains("连接与设置", error.Message);
        Assert.DoesNotContain(missing, error.Message);
        Assert.False(runtime.Alive);
        Assert.Null(runtime.Executor);
        Assert.Null(runtime.Gateway);
    }

    [Fact]
    public void Relative_profile_is_not_resolved_against_the_launch_directory()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ConnectionFiles.Read("connection.json", "盒子配对文件"));
        Assert.Contains("路径无效", error.Message);
    }

    [Fact]
    public void Missing_secret_is_identified_without_regenerating_credentials()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gateway.secrets.json");
        Assert.Contains("未找到网关凭据文件", Assert.Throws<InvalidOperationException>(() => ConnectionFiles.Read(missing, "网关凭据文件")).Message);
        Assert.False(File.Exists(missing));
    }
}
