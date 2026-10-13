namespace ClawBox.Executor.Persistence;

public static class InstanceLease
{
    public static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "executor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("Another executor is using this data directory.", error); }
    }
}
