using ClawBox.Executor.Api;
using ClawBox.Executor.Files;

namespace ClawBox.Executor.Tests;

public sealed class PathPolicyTests
{
    [Fact]
    public void Accepts_files_inside_root_but_rejects_traversal_and_prefix_collision()
    {
        using TestWorkspace temp = new();
        PathPolicy policy = new(temp.Options);
        Assert.Equal(temp.FilePath("hello.txt"), policy.Resolve(temp.FilePath("hello.txt")));
        Assert.Throws<ExecutorException>(() => policy.Resolve(Path.Combine(temp.Workspace, "..", "outside.txt")));
        Assert.Throws<ExecutorException>(() => policy.Resolve(temp.Workspace + "-other" + Path.DirectorySeparatorChar + "secret.txt"));
        Assert.Throws<ExecutorException>(() => policy.Resolve("relative.txt"));
    }

    [Fact]
    public void Rejects_symbolic_link_escape_even_when_target_is_an_allowed_directory()
    {
        using TestWorkspace temp = new();
        string link = temp.FilePath("link");
        // A junction on Windows does not require developer-mode symbolic-link privileges.
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { ArgumentList = { "/c", "mklink", "/J", link, temp.Workspace }, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, temp.Workspace);
        try { Assert.Throws<ExecutorException>(() => new PathPolicy(temp.Options).Resolve(Path.Combine(link, "a.txt"))); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Windows_rejects_network_device_and_alternate_data_stream_paths()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TestWorkspace temp = new();
        PathPolicy policy = new(temp.Options);
        foreach (string path in new[] { @"\\server\share\file.txt", @"\\?\C:\file.txt", temp.FilePath("a.txt:stream"), temp.FilePath("con.txt"), temp.FilePath("LPT1"), temp.FilePath("bad?.txt"), temp.FilePath("a.\b.txt") })
            Assert.Throws<ExecutorException>(() => policy.Resolve(path));
    }
}
