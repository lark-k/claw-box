using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Files;

namespace ClawBox.Executor.Projects;

public sealed class ProjectCommands(PathPolicy policy, AtomicFiles files, ExecutorOptions options)
{
    public static bool Supported(string operation) => operation is "npm.install" or "npm.ci" or "build" or "test" or "git.status" or "git.diff";
    public JsonElement Execute(string path, string operation, string expected, CancellationToken cancellation)
    {
        if (!Supported(operation)) throw new ExecutorException("invalid_arguments", 422, "Unsupported fixed project operation.");
        if (operation is "build" or "test") return new ProjectChecks(policy, options).Execute(path, operation, expected, cancellation);
        ProjectTree tree = new(policy);
        ProjectSnapshot source = tree.Read(path, cancellation);
        if (source.Sha256 != expected) throw new ExecutorException("project_changed", 409, "Obtain a fresh project snapshot.");
        if (operation.StartsWith("git.", StringComparison.Ordinal)) return Git(source, operation, cancellation);
        string packagePath = policy.Resolve(Path.Combine(source.Root, "package.json"));
        SourceFile packageFile = source.Files.SingleOrDefault(f => f.RelativePath == "package.json") ?? throw new ExecutorException("project_type_ambiguous", 422, "npm requires package.json.");
        JsonObject package = JsonNode.Parse(packageFile.Bytes)!.AsObject();
        ValidateRegistry(package);
        SourceFile? lockFile = source.Files.FirstOrDefault(f => f.RelativePath == "package-lock.json");
        if (operation == "npm.ci" && lockFile is null) throw new ExecutorException("project_lock_missing", 422, "npm ci requires package-lock.json. Use npm.install first.");
        if (lockFile is not null) ValidateRegistry(JsonNode.Parse(lockFile.Bytes)!);
        if (!OperatingSystem.IsWindows() || !File.Exists(options.ProjectRuntimeFile)) throw new ExecutorException("project_runtime_missing", 422, "Configure Node/npm first.");
        ProjectRuntimes runtimes = JsonSerializer.Deserialize<ProjectRuntimes>(File.ReadAllText(options.ProjectRuntimeFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        string node = Path.Combine(runtimes.NodeRoot, "node.exe"), npm = Path.Combine(runtimes.NodeRoot, "node_modules", "npm");
        if (!File.Exists(node) || !File.Exists(Path.Combine(npm, "bin", "npm-cli.js"))) throw new ExecutorException("project_runtime_missing", 422, "Configure Node/npm first.");
        using NativeSandbox sandbox = new();
        string work = Path.Combine(sandbox.Root, "work"), runtime = Path.Combine(sandbox.Root, "node");
        Directory.CreateDirectory(runtime); File.Copy(node, Path.Combine(runtime, "node.exe"));
        ProjectChecks.CopyCache(npm, Path.Combine(runtime, "node_modules", "npm"), cancellation);
        File.WriteAllText(Path.Combine(runtime, "appcontainer-fs.cjs"), ProjectChecks.Asset("NodeBootstrap"));
        File.WriteAllBytes(Path.Combine(work, "package.json"), packageFile.Bytes);
        if (lockFile is not null) File.WriteAllBytes(Path.Combine(work, "package-lock.json"), lockFile.Bytes);
        string userConfig = Path.Combine(sandbox.Root, "user.npmrc"), globalConfig = Path.Combine(sandbox.Root, "global.npmrc");
        File.WriteAllText(userConfig, ""); File.WriteAllText(globalConfig, "");
        Dictionary<string, string> environment = new()
        {
            ["PATH"] = runtime + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System),
            ["CI"] = "true",
            ["NODE_OPTIONS"] = "--preserve-symlinks --preserve-symlinks-main --require \"" + Path.Combine(runtime, "appcontainer-fs.cjs").Replace('\\', '/') + "\""
        };
        string[] arguments = [Path.Combine(runtime, "node_modules", "npm", "bin", "npm-cli.js"), operation == "npm.ci" ? "ci" : "install",
            "--ignore-scripts", "--no-audit", "--no-fund", "--registry=https://registry.npmjs.org", "--userconfig=" + userConfig,
            "--globalconfig=" + globalConfig, "--cache=" + Path.Combine(sandbox.Root, "cache"), "--fetch-retries=1", "--fetch-timeout=60000"];
        SandboxResult result = sandbox.Run(Path.Combine(runtime, "node.exe"), arguments, environment, cancellation, 300, internet: true);
        if (result.ExitCode != 0) return JsonSerializer.SerializeToElement(new { operation, path = source.Root, exitCode = result.ExitCode, commandPassed = false, output = result.Output, result.OutputTruncated, isolation = "windows-appcontainer", scriptsExecuted = false });
        string generatedLock = Path.Combine(work, "package-lock.json");
        ValidateRegistry(JsonNode.Parse(File.ReadAllText(generatedLock))!);
        if (tree.Read(source.Root, cancellation).Sha256 != expected) throw new ExecutorException("project_changed", 409, "Project changed during installation; original dependencies preserved.");
        string modules = policy.Resolve(Path.Combine(source.Root, "node_modules"));
        string stage = policy.Resolve(Path.Combine(source.Root, ".qh-dependencies-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(work, "node_modules"));
        ProjectChecks.CopyCache(Path.Combine(work, "node_modules"), stage, cancellation);
        string backup = policy.Resolve(Path.Combine(source.Root, ".qh-dependencies-backup-" + Guid.NewGuid().ToString("N")));
        if (tree.Read(source.Root, cancellation).Sha256 != expected) throw new ExecutorException("project_changed", 409, "Project changed before dependency publication.");
        cancellation.ThrowIfCancellationRequested();
        bool moved = false, published = false;
        try
        {
            if (Directory.Exists(modules)) { Directory.Move(modules, backup); moved = true; }
            Directory.Move(stage, modules);
            published = true;
            if (operation == "npm.install") files.Write(policy.Resolve(Path.Combine(source.Root, "package-lock.json")), File.ReadAllBytes(generatedLock), lockFile?.Sha256);
        }
        catch
        {
            if (published && Directory.Exists(modules)) Directory.Move(modules, stage);
            if (moved) Directory.Move(backup, modules);
            throw;
        }
        return JsonSerializer.SerializeToElement(new { operation, path = source.Root, exitCode = 0, commandPassed = true, output = result.Output, result.OutputTruncated, isolation = "windows-appcontainer", scriptsExecuted = false, dependencyBackup = moved ? backup : null });
    }
    public static void ValidateRegistry(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("workspaces") || obj["link"]?.ToString() == "true" || obj.ContainsKey("overrides")) throw Rejected();
            foreach ((string key, JsonNode? value) in obj)
            {
                if (value is null) continue;
                if (key == "resolved" && value.ToString().Length > 0 && !Official(value.ToString())) throw Rejected();
                if (key is "dependencies" or "devDependencies" or "optionalDependencies" or "peerDependencies")
                    foreach (JsonNode? spec in value.AsObject().Select(p => p.Value))
                        if (spec is JsonValue && !Regex.IsMatch(spec.ToString(), @"^[0-9A-Za-z.*~^<>=|\s+-]+$", RegexOptions.CultureInvariant)) throw Rejected();
                ValidateRegistry(value);
            }
        }
        else if (node is JsonArray array) foreach (JsonNode? value in array) if (value is not null) ValidateRegistry(value);
    }
    private static bool Official(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" && uri.Host == "registry.npmjs.org" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private JsonElement Git(ProjectSnapshot source, string operation, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new ExecutorException("project_runtime_missing", 422, "Windows is required.");
        string gitDirectory = policy.ResolveDirectory(Path.Combine(source.Root, ".git"));
        if (!Directory.Exists(gitDirectory)) throw new ExecutorException("project_git_missing", 422, "Select a Git repository root; linked worktrees are unsupported.");
        string? executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(root => Path.Combine(root.Trim('"'), "git.exe")).FirstOrDefault(File.Exists);
        if (executable is null) throw new ExecutorException("project_runtime_missing", 422, "Install Git for Windows first.");
        string runtime = Path.GetDirectoryName(executable)!;
        if (Path.GetFileName(runtime).Equals("cmd", StringComparison.OrdinalIgnoreCase)) runtime = Path.GetDirectoryName(runtime)!;
        using NativeSandbox sandbox = new();
        string ownedRuntime = Path.Combine(sandbox.Root, "git");
        ProjectChecks.CopyCache(runtime, ownedRuntime, cancellation);
        executable = Path.Combine(ownedRuntime, Path.GetRelativePath(runtime, executable));
        string work = Path.Combine(sandbox.Root, "work"), ownedGit = Path.Combine(work, ".git");
        ProjectChecks.CopyCache(gitDirectory, ownedGit, cancellation);
        File.WriteAllText(Path.Combine(ownedGit, "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
        foreach (SourceFile file in source.Files)
        {
            string target = Path.Combine(work, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, file.Bytes);
        }
        List<string> arguments = ["--no-optional-locks", "--literal-pathspecs", "--git-dir=.git", "--work-tree=.",
            "-c", "safe.directory=*", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + sandbox.Root,
            "-c", "core.pager=", "-c", "credential.helper=", "-c", "diff.external="];
        arguments.AddRange(operation == "git.status" ? ["status", "--porcelain=v1", "--untracked-files=no", "--ignore-submodules=all"] : ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--ignore-submodules=all"]);
        arguments.Add("--"); arguments.AddRange(source.Files.Select(file => file.RelativePath));
        if (source.Files.Length == 0) return JsonSerializer.SerializeToElement(new { operation, exitCode = 0, commandPassed = true, output = "No eligible source files." });
        SandboxResult result = sandbox.Run(executable, arguments, new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(sandbox.Root, "empty-gitconfig"), ["GIT_TERMINAL_PROMPT"] = "0" }, cancellation, 30);
        return JsonSerializer.SerializeToElement(new { operation, path = source.Root, exitCode = result.ExitCode, commandPassed = result.ExitCode == 0, output = result.Output, result.OutputTruncated, isolation = "windows-appcontainer" });
    }
    private static ExecutorException Rejected() => new("dependency_source_unsupported", 422, "Only npm registry dependencies without workspaces, overrides or links are supported.");
}
