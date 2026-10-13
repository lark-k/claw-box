using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Files;

namespace ClawBox.Executor.Projects;

public sealed record ProjectCommand(string Stack, string Executable, string[] Arguments, Dictionary<string, string> Environment, string[] ReadRoots);
public sealed class ProjectRuntimes
{
    public string DotnetRoot { get; set; } = "";
    public string NodeRoot { get; set; } = "";
    public string JavaHome { get; set; } = "";
    public string MavenHome { get; set; } = "";
    public string GradleHome { get; set; } = "";
    public string NugetCache { get; set; } = "";
    public string MavenCache { get; set; } = "";
    public string GradleCache { get; set; } = "";
}

public sealed class ProjectChecks(PathPolicy policy, ExecutorOptions options)
{
    public JsonElement Execute(string path, string kind, string expected, CancellationToken cancellation, int timeoutSeconds = 180)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(options.ProjectRuntimeFile) || !File.Exists(options.ProjectRuntimeFile))
            throw new ExecutorException("project_runtime_missing", 422, "Configure the native project runtimes first.");
        ProjectSnapshot source = new ProjectTree(policy).Read(path, cancellation);
        if (!string.Equals(source.Sha256, expected, StringComparison.Ordinal))
            throw new ExecutorException("project_changed", 409, "Project changed after the approved snapshot. Obtain a new snapshot and approval.");
        ProjectRuntimes runtimes = JsonSerializer.Deserialize<ProjectRuntimes>(File.ReadAllText(options.ProjectRuntimeFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ExecutorException("project_runtime_missing", 422, "Runtime configuration is missing.");
        using NativeSandbox sandbox = new();
        string work = Path.Combine(sandbox.Root, "work");
        foreach (SourceFile file in source.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(work, file.RelativePath));
            if (!target.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid snapshot path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, file.Bytes);
        }
        // Node resolves every ancestor of its CLI path. Keep its small public runtime inside the profile
        // instead of broadening access to the host volume and user directory metadata.
        if (File.Exists(Path.Combine(work, "package.json")))
        {
            string node = Require(runtimes.NodeRoot, "node.exe");
            string npm = Path.Combine(runtimes.NodeRoot, "node_modules", "npm");
            string runtime = Path.Combine(sandbox.Root, "node");
            Directory.CreateDirectory(runtime);
            File.Copy(node, Path.Combine(runtime, "node.exe"));
            CopyCache(npm, Path.Combine(runtime, "node_modules", "npm"), cancellation);
            // DOS-volume canonicalization is restricted in AppContainer. The adapter falls back
            // only inside this private snapshot after checking every component for links.
            File.WriteAllText(Path.Combine(runtime, "appcontainer-fs.cjs"), Asset("NodeBootstrap"));
            runtimes.NodeRoot = runtime;
        }
        if (File.Exists(Path.Combine(work, "pom.xml")))
        {
            // Maven's root discovery otherwise enumerates ancestors outside its workspace.
            Directory.CreateDirectory(Path.Combine(work, ".mvn"));
            string maven = Path.Combine(sandbox.Root, "maven");
            CopyCache(runtimes.MavenHome, maven, cancellation);
            runtimes.MavenHome = maven;
        }
        ProjectCommand command = Plan(work, kind, runtimes);
        // The built-in Node test runner normally forks with an IPC channel. Keep those tests
        // in the isolated process instead; change only the disposable execution copy.
        if (command.Stack == "node" && kind == "test")
        {
            string packagePath = Path.Combine(work, "package.json");
            var package = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(packagePath))!;
            string script = package["scripts"]!["test"]!.GetValue<string>();
            if (script.StartsWith("node --test", StringComparison.Ordinal) && (script.Length == 11 || char.IsWhiteSpace(script[11])))
            {
                package["scripts"]!["test"] = "node --experimental-test-isolation=none --test" + script[11..];
                File.WriteAllText(packagePath, package.ToJsonString());
            }
        }
        if (command.Stack == "node" && kind == "build")
        {
            string packagePath = Path.Combine(work, "package.json");
            var package = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(packagePath))!;
            string script = package["scripts"]!["build"]!.GetValue<string>();
            if (script == "vite build")
            {
                package["scripts"]!["build"] = "vite build --configLoader native";
                File.WriteAllText(packagePath, package.ToJsonString());
            }
        }
        foreach (string root in command.ReadRoots.Distinct(StringComparer.OrdinalIgnoreCase)) sandbox.AllowRead(root);
        if (command.Stack == "node" && Directory.Exists(Path.Combine(source.Root, "node_modules")))
            CopyCache(policy.Resolve(Path.Combine(source.Root, "node_modules")), Path.Combine(work, "node_modules"), cancellation);
        if (command.Stack == "node")
        {
            string esbuild = Path.Combine(work, "node_modules", "esbuild", "lib", "main.js");
            if (File.Exists(esbuild)) File.AppendAllText(esbuild, "\nconst quantumOriginal=module.exports;module.exports={...quantumOriginal,transform:async(...a)=>quantumOriginal.transformSync(...a),build:async(...a)=>quantumOriginal.buildSync(...a),formatMessages:async(...a)=>quantumOriginal.formatMessagesSync(...a),analyzeMetafile:async(...a)=>quantumOriginal.analyzeMetafileSync(...a)};\n");
        }
        if (command.Stack == "maven" && Directory.Exists(runtimes.MavenCache))
            CopyCache(runtimes.MavenCache, Path.Combine(sandbox.Root, "maven-cache"), cancellation);
        if (command.Stack == "gradle" && Directory.Exists(runtimes.GradleCache))
            CopyCache(runtimes.GradleCache, Path.Combine(sandbox.Root, "gradle-cache"), cancellation);
        if (command.Stack is "maven" or "gradle")
        {
            string adapter = Path.Combine(sandbox.Root, "java-adapter"); Directory.CreateDirectory(adapter);
            string sourceFile = Path.Combine(adapter, "QuantumFileSystemProvider.java"); File.WriteAllText(sourceFile, Asset("JavaFilesystemProvider"));
            CompileOwnedJavaAdapter(runtimes.JavaHome, adapter, sourceFile, cancellation);
            List<string> args = command.Arguments.ToList();
            int classpath = args.IndexOf("-classpath"); args[classpath + 1] = adapter + ";" + args[classpath + 1];
            args.Insert(0, "-Djava.nio.file.spi.DefaultFileSystemProvider=quantum.runtime.QuantumFileSystemProvider");
            command = command with { Arguments = args.ToArray() };
            command.Environment["QUANTUM_REALPATH_ROOTS"] = sandbox.Root + ";" + runtimes.JavaHome;
        }
        SandboxResult result = sandbox.Run(command.Executable, command.Arguments, command.Environment, cancellation, timeoutSeconds);
        bool testsObserved = kind != "test" || HasTestEvidence(command.Stack, result.Output);
        return JsonSerializer.SerializeToElement(new
        {
            path = source.Root,
            kind,
            stack = command.Stack,
            snapshotSha256 = source.Sha256,
            exitCode = result.ExitCode,
            checksPassed = result.ExitCode == 0 && testsObserved,
            testsObserved,
            output = result.Output,
            outputTruncated = result.OutputTruncated,
            isolation = "windows-appcontainer",
            originalProjectModified = false
        });
    }
    public static ProjectCommand Plan(string work, string kind, ProjectRuntimes runtimes)
    {
        if (kind is not ("build" or "test")) throw Invalid();
        Dictionary<string, string> environment = new() { ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System) };
        if (File.Exists(Path.Combine(work, "package.json")))
        {
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "package.json")));
            if (!package.RootElement.TryGetProperty("scripts", out JsonElement scripts) || !scripts.TryGetProperty(kind, out JsonElement script) || script.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(script.GetString()))
                throw new ExecutorException("project_script_missing", 422, "The requested build/test script is not defined in package.json.");
            string node = Require(runtimes.NodeRoot, "node.exe"), npm = Require(runtimes.NodeRoot, "node_modules/npm/bin/npm-cli.js");
            environment["PATH"] = runtimes.NodeRoot + ";" + environment["PATH"];
            environment["npm_config_cache"] = Path.Combine(Path.GetDirectoryName(work)!, "npm-cache");
            environment["npm_config_offline"] = "true"; environment["CI"] = "true";
            environment["ESBUILD_WORKER_THREADS"] = "0";
            environment["NODE_OPTIONS"] = "--preserve-symlinks --preserve-symlinks-main --require \"" + Path.Combine(runtimes.NodeRoot, "appcontainer-fs.cjs").Replace('\\', '/') + "\"";
            return new("node", node, [npm, "run", kind], environment, [runtimes.NodeRoot]);
        }
        if (File.Exists(Path.Combine(work, "pom.xml")))
        {
            string java = Require(runtimes.JavaHome, "bin/java.exe");
            string[] jars = Directory.Exists(Path.Combine(runtimes.MavenHome, "boot")) ? Directory.GetFiles(Path.Combine(runtimes.MavenHome, "boot"), "plexus-classworlds-*.jar") : [];
            if (jars.Length != 1) throw Missing();
            string config = Require(runtimes.MavenHome, "bin/m2.conf");
            environment["JAVA_HOME"] = runtimes.JavaHome;
            environment["PATH"] = Path.Combine(runtimes.JavaHome, "bin") + ";" + environment["PATH"];
            return new("maven", java, ["-Duser.dir=" + work, "-Duser.home=" + Path.GetDirectoryName(work)!, "-Djava.io.tmpdir=" + Path.Combine(Path.GetDirectoryName(work)!, "temp"), "-Dfile.encoding=UTF-8", "-classpath", jars[0], "-Dclassworlds.conf=" + config, "-Dmaven.home=" + runtimes.MavenHome,
                "-Dmaven.multiModuleProjectDirectory=" + work, "org.codehaus.plexus.classworlds.launcher.Launcher", "--batch-mode", "--offline",
                "-Dmaven.repo.local=" + Path.Combine(Path.GetDirectoryName(work)!, "maven-cache"), kind == "test" ? "test" : "package",
                kind == "test" ? "-DskipTests=false" : "-DskipTests=true", "-DforkCount=0"], environment, [runtimes.JavaHome, runtimes.MavenHome]);
        }
        if (File.Exists(Path.Combine(work, "build.gradle")) || File.Exists(Path.Combine(work, "build.gradle.kts")))
        {
            string java = Require(runtimes.JavaHome, "bin/java.exe");
            string[] jars = Directory.Exists(Path.Combine(runtimes.GradleHome, "lib")) ? Directory.GetFiles(Path.Combine(runtimes.GradleHome, "lib"), "gradle-launcher-*.jar") : [];
            if (jars.Length != 1) throw Missing();
            environment["JAVA_HOME"] = runtimes.JavaHome;
            environment["GRADLE_USER_HOME"] = Path.Combine(Path.GetDirectoryName(work)!, "gradle-cache");
            return new("gradle", java, ["-classpath", jars[0], "org.gradle.launcher.GradleMain", "--offline", "--no-daemon", kind], environment, [runtimes.JavaHome, runtimes.GradleHome]);
        }
        string[] solutions = Directory.GetFiles(work, "*.sln").Concat(Directory.GetFiles(work, "*.slnx")).ToArray();
        string[] projects = solutions.Length == 1 ? solutions : Directory.GetFiles(work, "*.csproj");
        if (projects.Length == 1)
        {
            string dotnet = Require(runtimes.DotnetRoot, "dotnet.exe");
            environment["DOTNET_ROOT"] = runtimes.DotnetRoot;
            environment["PATH"] = runtimes.DotnetRoot + ";" + environment["PATH"];
            string cache = string.IsNullOrWhiteSpace(runtimes.NugetCache) ? Path.Combine(Path.GetDirectoryName(work)!, "offline-packages") : runtimes.NugetCache;
            string[] roots = string.IsNullOrWhiteSpace(runtimes.NugetCache) ? [runtimes.DotnetRoot] : [runtimes.DotnetRoot, runtimes.NugetCache];
            return new("dotnet", dotnet, [kind, projects[0], "--configuration", "Release", "--nologo", "--disable-build-servers",
                "-p:RestoreSources=" + cache, "-p:RestoreIgnoreFailedSources=false", "-p:NuGetAudit=false"], environment, roots);
        }
        throw new ExecutorException("project_type_ambiguous", 422, "Select a project directory containing package.json, pom.xml, build.gradle or exactly one csproj.");
    }
    internal static string Asset(string name)
    {
        using Stream stream = typeof(ProjectChecks).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Runtime asset is missing.");
        using StreamReader reader = new(stream); return reader.ReadToEnd();
    }
    private static void CompileOwnedJavaAdapter(string javaHome, string adapter, string source, CancellationToken cancellation)
    {
        // Compile only this assembly's fixed resource, never project sources or processors.
        // The resulting provider enables the isolated JVM to bootstrap its filesystem.
        ProcessStartInfo info = new(Require(javaHome, "bin/javac.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = adapter,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment.Clear();
        info.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        info.Environment["WINDIR"] = info.Environment["SystemRoot"];
        info.Environment["TEMP"] = adapter; info.Environment["TMP"] = adapter;
        foreach (string argument in new[] { "-J-Xmx256m", "-J-Djava.io.tmpdir=" + adapter, "-J-Duser.home=" + adapter,
            "-proc:none", "--release", "21", "-sourcepath", adapter, "-classpath", adapter, "-d", adapter, source }) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new IOException("Java adapter compiler did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new ExecutorException("java_adapter_failed", 422, "The installed JDK must support the Java 21 runtime adapter.");
    }
    public static bool HasTestEvidence(string stack, string output)
    {
        string clean = Regex.Replace(output, "\u001b\\[[0-9;]*m", "");
        if (stack == "maven")
            return Regex.Matches(clean, @"Tests run:\s*(\d+),\s*Failures:\s*(\d+),\s*Errors:\s*(\d+),\s*Skipped:\s*(\d+)")
                .Any(match => int.TryParse(match.Groups[1].Value, out int total) &&
                    int.TryParse(match.Groups[4].Value, out int skipped) && total > skipped &&
                    match.Groups[2].Value == "0" && match.Groups[3].Value == "0");
        string pattern = stack switch
        {
            "node" => @"^# pass\s+[1-9][0-9]*\s*$|Tests:?\s+[1-9][0-9]*\s+passed\b",
            "gradle" => @"[1-9][0-9]* tests? completed",
            "dotnet" => @"(?:Passed!|Test summary:)[^\r\n]*[1-9][0-9]*",
            _ => "(?!)"
        };
        return Regex.IsMatch(clean, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }
    private static string Require(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root)) throw Missing();
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!File.Exists(file)) throw Missing();
        return file;
    }
    private static void CopyCache(string source, string target, CancellationToken cancellation)
    {
        long bytes = 0; int count = 0;
        Stack<(string Source, string Target)> pending = new(); pending.Push((source, target));
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory.Source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Dependency links are unsupported.");
            Directory.CreateDirectory(directory.Target);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Source))
            {
                cancellation.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Dependency links are unsupported.");
                string name = Path.GetFileName(entry);
                if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(entry) is ".key" or ".pem" or ".pfx" || name is ".npmrc" or "settings.xml" or "_remote.repositories" || name.EndsWith(".lastUpdated", StringComparison.Ordinal)) continue;
                string output = Path.Combine(directory.Target, name);
                if (Directory.Exists(entry)) { pending.Push((entry, output)); continue; }
                bytes += new FileInfo(entry).Length;
                if (++count > 50000 || bytes > 1024L * 1024 * 1024) throw new ExecutorException("dependency_cache_too_large", 413, "Offline dependency cache exceeds the snapshot limit.");
                File.Copy(entry, output, false);
            }
        }
    }
    private static ExecutorException Missing() => new("project_runtime_missing", 422, "The detected language runtime is not configured. Run Setup-ProjectRuntimes.ps1.");
    private static ExecutorException Invalid() => new("invalid_arguments", 422, "Only fixed build and test checks are supported.");
}
