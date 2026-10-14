using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ClawBox.Executor.Projects;

/// <summary>Windows isolation boundary. No inherited user environment; network is off unless the fixed installer requests internetClient.</summary>
[SupportedOSPlatform("windows")]
public sealed class NativeSandbox : IDisposable
{
    private readonly string profile = "qh-" + Guid.NewGuid().ToString("N");
    private readonly List<(string Path, FileSystemAccessRule Rule)> grants = [];
    private IntPtr sid;
    public string Root { get; }
    public NativeSandbox()
    {
        Marshal.ThrowExceptionForHR(CreateAppContainerProfile(profile, profile, "Isolated project check", IntPtr.Zero, 0, out sid));
        try
        {
            // A packaged parent can return a virtualized path containing '#!' and exceeding
            // legacy compiler path limits. Use a short, private, explicitly labelled directory.
            string parent = WorkBase();
            DirectoryInfo baseDirectory = Directory.CreateDirectory(parent);
            if ((baseDirectory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                baseDirectory.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Equals(WindowsIdentity.GetCurrent().User) != true)
                throw new IOException("The isolation directory has an unexpected owner or uses a link.");
            DirectorySecurity baseAcl = new(); baseAcl.SetAccessRuleProtection(true, false);
            baseAcl.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            baseAcl.AddAccessRule(new(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            baseDirectory.SetAccessControl(baseAcl);
            Root = Path.Combine(parent, profile);
            DirectoryInfo directory = Directory.CreateDirectory(Root);
            DirectorySecurity acl = new();
            acl.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier identity in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(sid) })
                acl.AddAccessRule(new(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            Check(ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out IntPtr label, out _));
            try { Check(SetFileSecurity(Root, 16, label)); }
            finally { LocalFree(label); }
            Directory.CreateDirectory(Path.Combine(Root, "work"));
            Directory.CreateDirectory(Path.Combine(Root, "temp"));
            Directory.CreateDirectory(Path.Combine(Root, "machine"));
        }
        catch { Dispose(); throw; }
    }
    public void AllowRead(string directory)
    {
        DirectoryInfo info = new(Path.GetFullPath(directory));
        if (Path.TrimEndingDirectorySeparator(info.FullName) == Path.GetPathRoot(info.FullName)) throw new IOException("Whole-drive runtime access is forbidden.");
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Runtime directory is missing or uses a link.");
        DirectorySecurity acl = info.GetAccessControl(AccessControlSections.Access);
        FileSystemAccessRule rule = new(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        acl.AddAccessRule(rule); info.SetAccessControl(acl); grants.Add((info.FullName, rule));
    }
    private static string WorkBase() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "QuantumHarnessChecks-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..16]);
    public SandboxResult Run(string executable, IReadOnlyList<string> arguments, IDictionary<string, string>? extraEnvironment,
        CancellationToken cancellation, int seconds = 120, bool internet = false)
    {
        SecurityAttributes inheritable = new() { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        Check(CreatePipe(out IntPtr read, out IntPtr write, ref inheritable, 0));
        using SafeFileHandle readHandle = new(read, true);
        using SafeFileHandle writeHandle = new(write, true);
        Check(SetHandleInformation(read, 1, 0));
        using SafeFileHandle input = CreateFile("NUL", 0x80000000, 3, ref inheritable, 3, 0, IntPtr.Zero);
        if (input.IsInvalid) throw new Win32Exception();
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal((int)size), caps = Marshal.AllocHGlobal(Marshal.SizeOf<Capabilities>()), handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
        IntPtr environment = IntPtr.Zero, job = IntPtr.Zero, internetSid = IntPtr.Zero, capability = IntPtr.Zero;
        ProcessInformation process = default;
        bool initialized = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); initialized = true;
            if (internet)
            {
                Check(ConvertStringSidToSid("S-1-15-3-1", out internetSid));
                capability = Marshal.AllocHGlobal(Marshal.SizeOf<SidAttribute>());
                Marshal.StructureToPtr(new SidAttribute { Sid = internetSid, Attributes = 4 }, capability, false);
            }
            Marshal.StructureToPtr(new Capabilities { Sid = sid, Sids = capability, Count = internet ? 1u : 0u }, caps, false);
            Marshal.WriteIntPtr(handles, input.DangerousGetHandle()); Marshal.WriteIntPtr(handles, IntPtr.Size, write);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20009, caps, (nuint)Marshal.SizeOf<Capabilities>(), IntPtr.Zero, IntPtr.Zero));
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero));
            Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["TEMP"] = Path.Combine(Root, "temp"),
                ["TMP"] = Path.Combine(Root, "temp"),
                ["LOCALAPPDATA"] = Root,
                ["APPDATA"] = Root,
                ["USERPROFILE"] = Root,
                ["ProgramFiles"] = Path.Combine(Root, "machine"),
                ["ProgramFiles(x86)"] = Path.Combine(Root, "machine"),
                ["ProgramW6432"] = Path.Combine(Root, "machine"),
                ["ProgramData"] = Path.Combine(Root, "machine"),
                ["SystemDrive"] = Path.GetPathRoot(Root)!.TrimEnd('\\'),
                ["HOMEDRIVE"] = Path.GetPathRoot(Root)!.TrimEnd('\\'),
                ["HOMEPATH"] = Root[(Path.GetPathRoot(Root)!.Length - 1)..],
                ["COMSPEC"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["DOTNET_CLI_HOME"] = Root,
                ["NUGET_PACKAGES"] = Path.Combine(Root, "packages"),
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1"
            };
            if (extraEnvironment is not null) foreach ((string name, string value) in extraEnvironment) variables[name] = value;
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + "\0\0");
            StartupInfoEx startup = new()
            {
                Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, Input = input.DangerousGetHandle(), Output = write, Error = write },
                Attributes = attributes
            };
            StringBuilder command = new(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08000000 | 0x80000 | 0x400 | 4, environment, Path.Combine(Root, "work"), ref startup, out process));
            // Do not resume until isolation and the kill-on-close job are proven.
            Check(OpenProcessToken(process.Process, 8, out IntPtr token));
            try { Check(GetTokenInformation(token, 29, out int isolated, 4, out _)); if (isolated != 1) throw new InvalidOperationException("AppContainer isolation was not established."); }
            finally { CloseHandle(token); }
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            JobLimits limits = new() { Basic = new() { Flags = 0x2000 | 0x200 | 8, ActiveProcesses = 32 }, JobMemory = (nuint)(2L * 1024 * 1024 * 1024) };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            Check(AssignProcessToJobObject(job, process.Process));
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            writeHandle.Dispose(); input.Dispose();
            using FileStream stream = new(readHandle, FileAccess.Read);
            Task<(string Text, bool Truncated)> output = Task.Run(() => ReadOutput(stream));
            Stopwatch clock = Stopwatch.StartNew();
            while (WaitForSingleObject(process.Process, 100) == 258)
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds > seconds)
                {
                    CloseHandle(job); job = IntPtr.Zero;
                    (string timedText, bool timedTruncated) = output.GetAwaiter().GetResult();
                    return new(124, timedText + "\nProject check timed out. The isolated process tree was terminated.", timedTruncated);
                }
            }
            Check(GetExitCodeProcess(process.Process, out uint exit));
            CloseHandle(job); job = IntPtr.Zero; // Also ends any surviving grandchildren holding stdout.
            (string text, bool truncated) = output.GetAwaiter().GetResult();
            return new((int)exit, text, truncated);
        }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.Process != IntPtr.Zero) { TerminateProcess(process.Process, 1); CloseHandle(process.Process); }
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(caps); Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (capability != IntPtr.Zero) Marshal.FreeHGlobal(capability);
            if (internetSid != IntPtr.Zero) FreeSid(internetSid);
        }
    }
    private static (string, bool) ReadOutput(Stream stream)
    {
        using StreamReader reader = new(stream);
        StringBuilder result = new(); char[] buffer = new char[2048]; int count; bool truncated = false;
        while ((count = reader.Read(buffer)) > 0)
        {
            result.Append(buffer, 0, count);
            if (result.Length > 65536) { result.Remove(0, result.Length - 65536); truncated = true; }
        }
        return (result.ToString(), truncated);
    }
    private static string Quote(string argument)
    {
        StringBuilder quoted = new("\""); int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); quoted.Append(c); slashes = 0;
        }
        quoted.Append('\\', slashes * 2); return quoted.Append('"').ToString();
    }
    public void Dispose()
    {
        foreach ((string path, FileSystemAccessRule rule) in grants.AsEnumerable().Reverse())
        {
            DirectoryInfo info = new(path); DirectorySecurity acl = info.GetAccessControl(AccessControlSections.Access);
            acl.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(acl);
        }
        grants.Clear();
        if (!string.IsNullOrEmpty(Root) && Directory.Exists(Root))
        {
            string parent = WorkBase();
            if (Path.GetFullPath(Root) != Path.Combine(parent, profile)) throw new IOException("Invalid isolation cleanup path.");
            Directory.Delete(Root, recursive: true);
        }
        if (sid != IntPtr.Zero) { FreeSid(sid); sid = IntPtr.Zero; DeleteAppContainerProfile(profile); }
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Capabilities { public IntPtr Sid, Sids; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAttribute { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSidToSid(string value, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    { public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string file, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JobLimits value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exit);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

public sealed record SandboxResult(int ExitCode, string Output, bool OutputTruncated);
