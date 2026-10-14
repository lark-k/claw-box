using System.Text.Json;
using System.Text.Json.Nodes;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Files;
using ClawBox.Executor.Projects;
using ClawBox.Executor.Tools;

namespace ClawBox.Executor.Tests;

[Collection("Native project runtimes")]
public sealed class ProjectCommandTests
{
    [Theory]
    [InlineData("file:../private")]
    [InlineData("https://example.com/pkg.tgz")]
    [InlineData("git+https://example.com/repo")]
    [InlineData("npm:other@1")]
    public void Installation_rejects_non_registry_sources(string spec) =>
        Assert.Throws<ExecutorException>(() => ProjectCommands.ValidateRegistry(JsonNode.Parse(JsonSerializer.Serialize(new { dependencies = new Dictionary<string, string> { ["pkg"] = spec } }))!));
    [Fact]
    public void Command_validation_rejects_arbitrary_commands_and_paths()
    {
        using TestWorkspace temp = new(); PathPolicy policy = new(temp.Options);
        ToolRegistry registry = new(policy, new(policy), new(temp.Options), temp.Options);
        Assert.Throws<ExecutorException>(() => registry.Validate(new(Guid.NewGuid(), "project.command", JsonSerializer.SerializeToElement(new { path = temp.Workspace, operation = "npm.install && whoami", expectedSnapshotSha256 = new string('A', 64) }))));
        Assert.Throws<ExecutorException>(() => registry.Validate(new(Guid.NewGuid(), "project.command", JsonSerializer.SerializeToElement(new { path = temp.Workspace, operation = "npm.install", expectedSnapshotSha256 = new string('A', 64), command = "whoami" }))));
        Assert.Throws<ExecutorException>(() => registry.Validate(new(Guid.NewGuid(), "project.command", JsonSerializer.SerializeToElement(new { path = Path.GetTempPath(), operation = "npm.install", expectedSnapshotSha256 = new string('A', 64) }))));
    }
    [Fact]
    public void Git_status_and_diff_are_read_only_and_disable_external_filters()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TestWorkspace temp = new();
        string git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "git.exe")).First(File.Exists);
        void Run(params string[] arguments)
        {
            System.Diagnostics.ProcessStartInfo info = new(git) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(temp.Workspace);
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!; string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, output + error);
        }
        Run("init"); File.WriteAllText(temp.FilePath("main.js"), "const value=1;"); Run("add", "main.js");
        foreach (string file in Directory.EnumerateFiles(temp.Workspace, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllText(temp.FilePath("main.js"), "const value=2;");
        PathPolicy policy = new(temp.Options); ProjectTree tree = new(policy); ProjectCommands commands = new(policy, new(policy), temp.Options);
        string snapshot = tree.Read(temp.Workspace, CancellationToken.None).Sha256;
        JsonElement status = commands.Execute(temp.Workspace, "git.status", snapshot, CancellationToken.None);
        Assert.True(status.GetProperty("commandPassed").GetBoolean(), status.GetProperty("output").GetString());
        Assert.Contains("main.js", status.GetProperty("output").GetString());
        JsonElement diff = commands.Execute(temp.Workspace, "git.diff", snapshot, CancellationToken.None);
        Assert.True(diff.GetProperty("commandPassed").GetBoolean(), diff.GetProperty("output").GetString());
        Assert.Contains("value=2", diff.GetProperty("output").GetString()); Assert.Equal("const value=2;", File.ReadAllText(temp.FilePath("main.js")));
        // Git object files are readonly; remove that attribute only in this owned fixture.
        foreach (string file in Directory.EnumerateFiles(temp.Workspace, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    }
    [Fact]
    public void Live_registry_install_and_vite5_build()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("QH_INSTALL_LIVE") != "1") return;
        using TestWorkspace temp = new();
        File.WriteAllText(temp.FilePath("package.json"), """{"name":"quantum-vue-live","version":"1.0.0","type":"module","scripts":{"build":"vite build"},"dependencies":{"vue":"3.5.13","vite":"5.4.21","@vitejs/plugin-vue":"5.2.4"}}""");
        File.WriteAllText(temp.FilePath("index.html"), "<div id=app></div><script type=module src=/main.js></script>");
        File.WriteAllText(temp.FilePath("main.js"), "import {createApp} from 'vue';import App from './App.vue';createApp(App).mount('#app');");
        File.WriteAllText(temp.FilePath("App.vue"), "<template><h1>Fresh fruit</h1></template>");
        File.WriteAllText(temp.FilePath("vite.config.js"), "import {defineConfig} from 'vite';import vue from '@vitejs/plugin-vue';export default defineConfig({plugins:[vue()]});");
        temp.Options.ProjectRuntimeFile = Path.Combine(temp.Options.DataDirectory, "runtimes.json"); Directory.CreateDirectory(temp.Options.DataDirectory);
        File.WriteAllText(temp.Options.ProjectRuntimeFile, JsonSerializer.Serialize(new ProjectRuntimes { NodeRoot = Path.GetDirectoryName(NativeSandboxTests.FindNode())! }));
        PathPolicy policy = new(temp.Options); ProjectTree tree = new(policy); ProjectCommands commands = new(policy, new(policy), temp.Options);
        JsonElement install = commands.Execute(temp.Workspace, "npm.install", tree.Read(temp.Workspace, CancellationToken.None).Sha256, CancellationToken.None);
        Assert.True(install.GetProperty("commandPassed").GetBoolean(), install.GetProperty("output").GetString());
        JsonElement build = commands.Execute(temp.Workspace, "build", tree.Read(temp.Workspace, CancellationToken.None).Sha256, CancellationToken.None);
        Assert.True(build.GetProperty("checksPassed").GetBoolean(), build.GetProperty("output").GetString());
        Assert.False(Directory.Exists(temp.FilePath("dist")));
    }
    [Fact]
    public void Npm_install_disables_scripts_and_publishes_lock_without_source_changes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TestWorkspace temp = new();
        const string package = """{"name":"quantum-install-test","version":"1.0.0","scripts":{"postinstall":"node -e \"require('fs').writeFileSync('hook-ran.txt','bad')\""}}""";
        File.WriteAllText(temp.FilePath("package.json"), package);
        temp.Options.ProjectRuntimeFile = Path.Combine(temp.Options.DataDirectory, "runtimes.json");
        Directory.CreateDirectory(temp.Options.DataDirectory);
        File.WriteAllText(temp.Options.ProjectRuntimeFile, JsonSerializer.Serialize(new ProjectRuntimes { NodeRoot = Path.GetDirectoryName(NativeSandboxTests.FindNode())! }));
        PathPolicy policy = new(temp.Options); ProjectTree tree = new(policy);
        ProjectCommands commands = new(policy, new(policy), temp.Options);
        JsonElement result = commands.Execute(temp.Workspace, "npm.install", tree.Read(temp.Workspace, CancellationToken.None).Sha256, CancellationToken.None);
        Assert.True(result.GetProperty("commandPassed").GetBoolean(), result.GetProperty("output").GetString());
        Assert.False(File.Exists(temp.FilePath("hook-ran.txt"))); Assert.Equal(package, File.ReadAllText(temp.FilePath("package.json")));
        Assert.True(File.Exists(temp.FilePath("package-lock.json")));
        JsonElement ci = commands.Execute(temp.Workspace, "npm.ci", tree.Read(temp.Workspace, CancellationToken.None).Sha256, CancellationToken.None);
        Assert.True(ci.GetProperty("commandPassed").GetBoolean(), ci.GetProperty("output").GetString());
        Assert.Throws<ExecutorException>(() => commands.Execute(temp.Workspace, "npm.install", new string('A', 64), CancellationToken.None));
    }
}
