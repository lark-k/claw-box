using System.Runtime.InteropServices;
using ClawBox.Executor.Projects;
using System.Text.Json;

namespace ClawBox.Executor.Tests;

[Collection("Native project runtimes")]
public sealed class NativeSandboxTests
{
    [Fact]
    public void Windows_child_process_stdio_is_available_inside_isolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        string node = FindNode();
        using NativeSandbox sandbox = new(); sandbox.AllowRead(Path.GetDirectoryName(node)!);
        string directory = Path.Combine(sandbox.Root, "node"); Directory.CreateDirectory(directory);
        string bootstrap = Path.Combine(directory, "bootstrap.cjs");
        using (Stream stream = typeof(ProjectChecks).Assembly.GetManifestResourceStream("NodeBootstrap")!)
        using (StreamReader reader = new(stream)) File.WriteAllText(bootstrap, reader.ReadToEnd());
        SandboxResult result = sandbox.Run(node, ["--preserve-symlinks", "--preserve-symlinks-main", "--require", bootstrap, "-e", "const r=require('node:child_process').spawnSync(process.execPath,['-e',\"console.log('child')\"],{timeout:2000,encoding:'utf8'});console.log(r.status,r.stdout,r.error?.code);process.exit(r.status===0&&r.stdout.trim()==='child'?0:8);"], null, CancellationToken.None, 8);
        Assert.True(result.ExitCode == 0, result.Output);
    }
    [Fact]
    public void Appcontainer_denies_network_even_when_host_listener_is_available()
    {
        if (!OperatingSystem.IsWindows()) return;
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using (System.Net.Sockets.TcpClient host = new())
        {
            host.Connect(System.Net.IPAddress.Loopback, port);
            using System.Net.Sockets.TcpClient accepted = listener.AcceptTcpClient();
            Assert.True(host.Connected);
        }
        string node = FindNode();
        using NativeSandbox sandbox = new(); sandbox.AllowRead(Path.GetDirectoryName(node)!);
        string program = "const s=require('node:net').connect(" + port + ", '127.0.0.1');s.on('connect',()=>process.exit(9));s.on('error',e=>{console.log(e.code);process.exit(['EACCES','EPERM','ETIMEDOUT'].includes(e.code)?0:8)});";
        SandboxResult result = sandbox.Run(node, ["-e", program], null, CancellationToken.None, 10);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(listener.Pending());
    }
    [Fact]
    public void Windows_sandbox_launches_runtime_without_user_environment()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = RuntimeEnvironment.GetRuntimeDirectory();
        for (int i = 0; i < 3; i++) root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!;
        using NativeSandbox sandbox = new();
        sandbox.AllowRead(root);
        SandboxResult result = sandbox.Run(Path.Combine(root, "dotnet.exe"), ["--info"], null, CancellationToken.None, 30);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(".NET", result.Output);
        Assert.False(result.OutputTruncated);
    }
    [Fact]
    public void Node_code_cannot_read_private_files_or_use_inherited_approval_tokens()
    {
        if (!OperatingSystem.IsWindows()) return;
        string node = FindNode();
        using TestWorkspace temp = new();
        string privatePath = temp.FilePath("credential.secrets.json");
        File.WriteAllText(privatePath, "PRIVATE_CREDENTIAL_FIXTURE");
        using NativeSandbox sandbox = new(); sandbox.AllowRead(Path.GetDirectoryName(node)!);
        string program = "const fs=require('node:fs');let denied=false;try{fs.readFileSync(" + JsonSerializer.Serialize(privatePath) + ")}catch(e){denied=true}if(!denied||process.env.Executor__AdminToken)process.exit(9);console.log('PRIVATE_ACCESS_DENIED');";
        SandboxResult result = sandbox.Run(node, ["-e", program], null, CancellationToken.None, 20);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PRIVATE_ACCESS_DENIED", result.Output);
        Assert.DoesNotContain("PRIVATE_CREDENTIAL_FIXTURE", result.Output);
    }
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Cancel_terminates_native_process_and_does_not_hang()
    {
        if (!OperatingSystem.IsWindows()) return;
        string node = FindNode();
        using NativeSandbox sandbox = new(); sandbox.AllowRead(Path.GetDirectoryName(node)!);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
        Assert.Throws<OperationCanceledException>(() => sandbox.Run(node, ["-e", "setInterval(()=>{},1000)"], null, cancellation.Token, 20));
    }
    internal static string FindNode()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string file = Path.Combine(directory.Trim('"'), "node.exe");
            if (!File.Exists(file)) continue;
            DirectoryInfo parent = new(directory.Trim('"'));
            return Path.Combine(parent.ResolveLinkTarget(true)?.FullName ?? parent.FullName, "node.exe");
        }
        throw new InvalidOperationException("Node.js is required by the repository CI runtime.");
    }
}
