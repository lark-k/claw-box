using System.Runtime.InteropServices;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Files;
using ClawBox.Executor.Projects;
using ClawBox.Executor.Tools;

namespace ClawBox.Executor.Tests;

[Collection("Native project runtimes")]
public sealed class ProjectTests
{
    [Theory]
    [InlineData("node", "# tests 0\n# pass 0", false)]
    [InlineData("node", "# tests 1\n# pass 0\n# skipped 1", false)]
    [InlineData("node", "# tests 1\n# pass 1", true)]
    [InlineData("node", "Tests:  2 passed, 2 total", true)]
    [InlineData("node", "Tests  3 passed (3)", true)]
    [InlineData("maven", "Tests run: 0, Failures: 0", false)]
    [InlineData("maven", "Tests run: 1, Failures: 0, Errors: 0, Skipped: 0", true)]
    [InlineData("maven", "Tests run: 1, Failures: 0, Errors: 0, Skipped: 1", false)]
    [InlineData("maven", "Tests run: 2, Failures: 1, Errors: 0, Skipped: 0", false)]
    [InlineData("dotnet", "Passed! - Failed: 0, Passed: 1, Total: 1", true)]
    public void Zero_tests_do_not_count_as_success(string stack, string output, bool expected) =>
        Assert.Equal(expected, ProjectChecks.HasTestEvidence(stack, output));
    [Fact]
    public void Snapshot_excludes_private_and_generated_files_and_changes_with_source()
    {
        using TestWorkspace temp = new();
        string root = Path.GetDirectoryName(temp.FilePath("package.json"))!;
        File.WriteAllText(temp.FilePath("package.json"), "{}");
        File.WriteAllText(temp.FilePath("main.js"), "export const value = 1;");
        File.WriteAllText(temp.FilePath(".env"), "PRIVATE");
        Directory.CreateDirectory(temp.FilePath("node_modules"));
        File.WriteAllText(temp.FilePath("node_modules/cache.js"), "cached");
        ProjectTree tree = new(new(temp.Options));
        ProjectSnapshot initial = tree.Read(root, CancellationToken.None);
        Assert.Equal(2, initial.Files.Length);
        Assert.Equal(initial.Sha256, tree.Read(root, CancellationToken.None).Sha256);
        File.WriteAllText(temp.FilePath("main.js"), "export const value = 2;");
        Assert.NotEqual(initial.Sha256, tree.Read(root, CancellationToken.None).Sha256);
        Assert.Throws<ExecutorException>(() => tree.Read(Path.GetDirectoryName(root)!, CancellationToken.None));
    }
    [Fact]
    public void Check_rejects_commands_and_stale_snapshots_before_execution()
    {
        using TestWorkspace temp = new();
        PathPolicy policy = new(temp.Options);
        ToolRegistry registry = new(policy, new(policy), new(temp.Options), temp.Options);
        string root = Path.GetDirectoryName(temp.FilePath("main.cs"))!;
        ToolCall invalid = new(Guid.NewGuid(), "project.check", JsonSerializer.SerializeToElement(new { path = root, kind = "shell", expectedSnapshotSha256 = new string('A', 64) }));
        Assert.Throws<ExecutorException>(() => registry.Validate(invalid));
        ToolCall injection = new(Guid.NewGuid(), "project.check", JsonSerializer.SerializeToElement(new { path = root, kind = "build", expectedSnapshotSha256 = new string('A', 64), command = "whoami" }));
        Assert.Throws<ExecutorException>(() => registry.Validate(injection));
        if (!OperatingSystem.IsWindows()) return;
        temp.Options.ProjectRuntimeFile = temp.FilePath("runtime.json");
        File.WriteAllText(temp.Options.ProjectRuntimeFile, "{}");
        ExecutorException stale = Assert.Throws<ExecutorException>(() => new ProjectChecks(policy, temp.Options).Execute(root, "build", new string('A', 64), CancellationToken.None));
        Assert.Equal("project_changed", stale.Code);
    }
    [Fact]
    public void Native_dotnet_build_uses_snapshot_and_preserves_original_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TestWorkspace temp = new();
        File.WriteAllText(temp.FilePath("Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(temp.FilePath("Calculator.cs"), "public class Calculator { public static int Add(int a, int b) => a + b; }");
        string root = Path.GetDirectoryName(temp.FilePath("Sample.csproj"))!;
        string sdk = RuntimeEnvironment.GetRuntimeDirectory();
        for (int i = 0; i < 3; i++) sdk = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sdk))!;
        // Runtime configuration belongs outside the selected project snapshot.
        temp.Options.ProjectRuntimeFile = Path.Combine(temp.Options.DataDirectory, "runtime.json");
        Directory.CreateDirectory(temp.Options.DataDirectory);
        File.WriteAllText(temp.Options.ProjectRuntimeFile, JsonSerializer.Serialize(new ProjectRuntimes { DotnetRoot = sdk }));
        ProjectTree tree = new(new(temp.Options)); ProjectSnapshot snapshot = tree.Read(root, CancellationToken.None);
        JsonElement result = new ProjectChecks(new(temp.Options), temp.Options).Execute(root, "build", snapshot.Sha256, CancellationToken.None);
        Assert.True(result.GetProperty("checksPassed").GetBoolean(), result.GetProperty("output").GetString());
        Assert.Equal("windows-appcontainer", result.GetProperty("isolation").GetString());
        Assert.False(Directory.Exists(temp.FilePath("bin")));
        Assert.False(Directory.Exists(temp.FilePath("obj")));
    }
    [Fact]
    public void Node_build_and_real_tests_return_exit_status_without_host_changes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TestWorkspace temp = new();
        string root = temp.Workspace;
        File.WriteAllText(temp.FilePath("package.json"), """{"scripts":{"build":"node build.cjs","test":"node --test math.test.cjs"}}""");
        File.WriteAllText(temp.FilePath("math.cjs"), "module.exports=(a,b)=>a+b;");
        File.WriteAllText(temp.FilePath("build.cjs"), "require('node:fs').writeFileSync('compiled.txt','compiled');");
        File.WriteAllText(temp.FilePath("math.test.cjs"), "const t=require('node:test');const a=require('node:assert/strict');const sum=require('./math.cjs');t('sum',()=>a.equal(sum(2,3),5));");
        temp.Options.ProjectRuntimeFile = Path.Combine(temp.Options.DataDirectory, "runtime.json");
        Directory.CreateDirectory(temp.Options.DataDirectory);
        File.WriteAllText(temp.Options.ProjectRuntimeFile, JsonSerializer.Serialize(new ProjectRuntimes { NodeRoot = Path.GetDirectoryName(NativeSandboxTests.FindNode())! }));
        ProjectTree tree = new(new(temp.Options)); string expected = tree.Read(root, CancellationToken.None).Sha256;
        ProjectChecks checks = new(new(temp.Options), temp.Options);
        JsonElement build = checks.Execute(root, "build", expected, CancellationToken.None);
        Assert.True(build.GetProperty("checksPassed").GetBoolean(), build.GetProperty("output").GetString());
        Assert.False(File.Exists(temp.FilePath("compiled.txt")));
        JsonElement test = checks.Execute(root, "test", expected, CancellationToken.None);
        Assert.True(test.GetProperty("checksPassed").GetBoolean(), test.GetProperty("output").GetString());
        File.WriteAllText(temp.FilePath("math.cjs"), "module.exports=(a,b)=>a-b;");
        expected = tree.Read(root, CancellationToken.None).Sha256;
        JsonElement failure = checks.Execute(root, "test", expected, CancellationToken.None);
        Assert.False(failure.GetProperty("checksPassed").GetBoolean());
        Assert.NotEqual(0, failure.GetProperty("exitCode").GetInt32());
    }
}
