using ClawBox.Executor.Configuration;
using Microsoft.Data.Sqlite;

namespace ClawBox.Executor.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClawBox.Tests", Guid.NewGuid().ToString("N"));
    public string Workspace => Path.Combine(Root, "workspace");
    public ExecutorOptions Options { get; }

    public TestWorkspace()
    {
        Directory.CreateDirectory(Workspace);
        Options = new()
        {
            DataDirectory = Path.Combine(Root, "state"),
            AllowedRoots = [Workspace],
            AdminToken = new string('a', 64),
            WorkerToken = new string('w', 64)
        };
    }

    public string FilePath(string name) => Path.Combine(Workspace, name);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
