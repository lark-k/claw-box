using System.Text.Json;
using ClawBox.Executor.Projects;

namespace ClawBox.Executor.Tests;

[Collection("Native project runtimes")]
public sealed class PolyglotTests
{
    [PolyglotTheory]
    [InlineData("react")]
    [InlineData("vue")]
    [InlineData("java")]
    public void Project_build_and_test_in_appcontainer(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        string fixtures = Environment.GetEnvironmentVariable("CLAWBOX_POLYGLOT_FIXTURES")!;
        string configuration = Path.Combine(fixtures, "runtime.json");
        {
            string project = Path.Combine(fixtures, name);
            using TestWorkspace temp = new();
            temp.Options.AllowedRoots = [project];
            temp.Options.ProjectRuntimeFile = configuration;
            ClawBox.Executor.Files.PathPolicy policy = new(temp.Options);
            string digest = new ProjectTree(policy).Read(project, CancellationToken.None).Sha256;
            ProjectChecks checks = new(policy, temp.Options);
            foreach (string kind in new[] { "build", "test" })
            {
                JsonElement result = checks.Execute(project, kind, digest, CancellationToken.None, 30);
                File.WriteAllText(Path.Combine(fixtures, name + "-" + kind + "-result.json"), result.GetRawText());
                Assert.True(result.GetProperty("checksPassed").GetBoolean(), name + ": " + kind + "\n" + result.GetProperty("output").GetString());
                Assert.Equal(digest, new ProjectTree(policy).Read(project, CancellationToken.None).Sha256);
            }
            Assert.False(Directory.Exists(Path.Combine(project, "dist")));
            Assert.False(Directory.Exists(Path.Combine(project, "target")));
        }
    }
}

public sealed class PolyglotTheoryAttribute : TheoryAttribute
{
    public PolyglotTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAWBOX_POLYGLOT_FIXTURES")))
            Skip = "Run scripts/ci/Test-ProjectRuntimes.ps1 to prepare the owned offline dependency fixtures.";
    }
}
