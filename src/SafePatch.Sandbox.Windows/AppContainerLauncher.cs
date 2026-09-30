using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SafePatch.Host;
using static SafePatch.Sandbox.Windows.Native;

namespace SafePatch.Sandbox.Windows;

public sealed record SandboxOptions
{
    /// <summary>Path to SafePatch.Worker.exe. Its directory is the only one the container is granted.</summary>
    public required string WorkerPath { get; init; }

    public string ContainerName { get; init; } = "SafePatch.Worker";

    /// <summary>
    /// The most memory the worker may commit; past it the Job Object ends it. <see cref="DefaultMemoryLimitBytes"/>
    /// unless <see cref="MemoryLimitVariable"/> sets another, which reaches patchers Synthesis runs too.
    /// </summary>
    public ulong MemoryLimitBytes { get; init; } = ConfiguredMemoryLimitBytes(Environment.GetEnvironmentVariable);

    /// <summary>4 GiB: a full parse of every record a patch touches in a large load order fits, with room to spare.</summary>
    public const ulong DefaultMemoryLimitBytes = 4UL * 1024 * 1024 * 1024;

    /// <summary>An environment variable giving the worker memory limit in MiB.</summary>
    public const string MemoryLimitVariable = "SAFEPATCH_WORKER_MEMORY_MB";

    private const ulong MinimumMemoryLimitMiB = 256;
    private const ulong MaximumMemoryLimitMiB = 1024 * 1024;

    /// <summary>The limit <see cref="MemoryLimitVariable"/> sets, or the default when it is unset.</summary>
    public static ulong ConfiguredMemoryLimitBytes(Func<string, string?> environment) =>
        environment(MemoryLimitVariable) is { Length: > 0 } value ? ParseMemoryLimit(value, MemoryLimitVariable) : DefaultMemoryLimitBytes;

    /// <summary>A memory limit given in MiB, in bytes. A value that is not a whole number of MiB in range is refused.</summary>
    /// <param name="source">Where the value came from, for the error.</param>
    public static ulong ParseMemoryLimit(string mebibytes, string source)
    {
        if (!ulong.TryParse(mebibytes.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value is < MinimumMemoryLimitMiB or > MaximumMemoryLimitMiB)
        {
            throw new SafePatchException($"{source} is \"{mebibytes}\": give the worker memory limit as a whole number of MiB from {MinimumMemoryLimitMiB} to {MaximumMemoryLimitMiB}, e.g. 4096 for 4 GiB.");
        }
        return value * 1024 * 1024;
    }

    /// <summary>
    /// Less Privileged AppContainer: also opts out of the ALL APPLICATION PACKAGES grants. Needs the
    /// .NET runtime directory to be explicitly readable by the container, so it is off by default.
    /// </summary>
    public bool LessPrivileged { get; init; }
}

/// <summary>
/// Launches the worker in an AppContainer with no capabilities (no network, no user files), in a
/// Job Object that kills it on close, allows one process and caps memory. It inherits only the
/// two pipe handles and a minimal environment. Any failure to set this up throws: never falls back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppContainerLauncher(SandboxOptions options) : IWorkerLauncher
{
    /// <summary>The most memory the last worker this launcher started committed at once, once it has ended. For tuning <see cref="SandboxOptions.MemoryLimitBytes"/>.</summary>
    public ulong? LastPeakMemoryBytes { get; private set; }

    public IWorkerProcess Launch(string nonce)
    {
        if (!File.Exists(options.WorkerPath)) throw new SafePatchException($"Worker not found at {options.WorkerPath}.");
        var workerDirectory = Path.GetDirectoryName(Path.GetFullPath(options.WorkerPath))!;

        var sid = GetOrCreateContainerSid(options.ContainerName);
        try
        {
            GrantReadExecute(workerDirectory, new SecurityIdentifier(sid));

            var toWorker = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var fromWorker = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            try
            {
                var commandLine = $"\"{options.WorkerPath}\" --nonce {nonce} --in {toWorker.GetClientHandleAsString()} --out {fromWorker.GetClientHandleAsString()}";
                var job = CreateJob();
                try
                {
                    var info = StartSuspended(commandLine, workerDirectory, sid,
                        [toWorker.ClientSafePipeHandle.DangerousGetHandle(), fromWorker.ClientSafePipeHandle.DangerousGetHandle()]);
                    var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
                    try
                    {
                        if (!AssignProcessToJobObject(job, info.hProcess)) throw Win32("AssignProcessToJobObject");
                        if (ResumeThread(info.hThread) == uint.MaxValue) throw Win32("ResumeThread");
                    }
                    catch
                    {
                        TerminateProcess(info.hProcess, 1);
                        process.Dispose();
                        throw;
                    }
                    finally
                    {
                        CloseHandle(info.hThread);
                    }

                    toWorker.DisposeLocalCopyOfClientHandle();
                    fromWorker.DisposeLocalCopyOfClientHandle();
                    return new SandboxedWorker(job, process, fromWorker, toWorker, peak => LastPeakMemoryBytes = peak);
                }
                catch
                {
                    job.Dispose();
                    throw;
                }
            }
            catch
            {
                toWorker.Dispose();
                fromWorker.Dispose();
                throw;
            }
        }
        finally
        {
            FreeSid(sid);
        }
    }

    /// <summary>
    /// Creates the container profile once, or finds the existing one. Creating the same profile from two places at
    /// once (parallel test runs, a CLI beside an MCP server) fails with E_UNEXPECTED, so creation is serialised
    /// across processes in the user's session.
    /// </summary>
    private static IntPtr GetOrCreateContainerSid(string name)
    {
        using var creating = new Mutex(initiallyOwned: false, $@"Local\SafePatch.AppContainer.{name}");
        try
        {
            creating.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // A process died while creating the profile: this one now owns the mutex and finishes the job.
        }
        try
        {
            var hr = CreateAppContainerProfile(name, name, "Sandbox for SafePatch worker programs", IntPtr.Zero, 0, out var sid);
            if (hr == HRESULT_ALREADY_EXISTS) hr = DeriveAppContainerSidFromAppContainerName(name, out sid);
            if (hr != 0) throw new SafePatchException($"Could not create AppContainer profile (0x{hr:X8}).");
            return sid;
        }
        finally
        {
            creating.ReleaseMutex();
        }
    }

    /// <summary>Lets the container load the worker and its libraries. Idempotent.</summary>
    private static void GrantReadExecute(string directory, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);

        var existing = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        if (existing.Any(r => r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow
                              && r.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute) && r.InheritanceFlags == rule.InheritanceFlags))
            return;

        security.AddAccessRule(rule);
        info.SetAccessControl(security);
    }

    private SafeFileHandle CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw Win32("CreateJobObject");

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                             | JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)options.MemoryLimitBytes,
        };
        SetJobInformation(job, JobObjectExtendedLimitInformation, limits);
        SetJobInformation(job, JobObjectBasicUIRestrictions, JOB_OBJECT_UILIMIT_ALL);
        return job;
    }

    private static void SetJobInformation<T>(SafeFileHandle job, int infoClass, T value) where T : unmanaged
    {
        unsafe
        {
            if (!SetInformationJobObject(job, infoClass, (IntPtr)(&value), sizeof(T))) throw Win32("SetInformationJobObject");
        }
    }

    private PROCESS_INFORMATION StartSuspended(string commandLine, string directory, IntPtr sid, IntPtr[] inheritedHandles)
    {
        // Under MO2, usvfs injects itself into every process started here by redirecting its first thread through a
        // stub that returns with a plain `ret`, which a CET shadow stack refuses. So under MO2 alone the worker starts
        // without shadow stacks; the stub then cannot load usvfs from inside the container and returns (ADR 002, "MO2").
        var underMo2 = Mo2Vfs.Active;
        var attributeCount = 3 + (options.LessPrivileged ? 1 : 0) + (underMo2 ? 1 : 0);
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
        var handles = Marshal.AllocHGlobal(IntPtr.Size * inheritedHandles.Length);
        var childPolicy = Marshal.AllocHGlobal(sizeof(uint));
        var packagesPolicy = Marshal.AllocHGlobal(sizeof(uint));
        var mitigationPolicy = Marshal.AllocHGlobal(2 * sizeof(ulong));
        var environment = Marshal.StringToHGlobalUni(EnvironmentBlock());
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size)) throw Win32("InitializeProcThreadAttributeList");
            initialized = true;

            Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = sid }, capabilities, false);
            Marshal.Copy(inheritedHandles, 0, handles, inheritedHandles.Length);
            Marshal.WriteInt32(childPolicy, (int)PROCESS_CREATION_CHILD_PROCESS_RESTRICTED);
            Marshal.WriteInt32(packagesPolicy, (int)PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT);

            Update(attributes, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, capabilities, Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Update(attributes, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, IntPtr.Size * inheritedHandles.Length);
            Update(attributes, PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, childPolicy, sizeof(uint));
            if (options.LessPrivileged)
                Update(attributes, PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY, packagesPolicy, sizeof(uint));
            if (underMo2)
            {
                Marshal.WriteInt64(mitigationPolicy, 0);
                Marshal.WriteInt64(mitigationPolicy, sizeof(ulong), (long)PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF);
                Update(attributes, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY, mitigationPolicy, 2 * sizeof(ulong));
            }

            var startup = new STARTUPINFOEX { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            if (!CreateProcess(null, (commandLine + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, true,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT,
                    environment, directory, ref startup, out var process))
                throw Win32("CreateProcess");

            return process;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(childPolicy);
            Marshal.FreeHGlobal(packagesPolicy);
            Marshal.FreeHGlobal(mitigationPolicy);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static void Update(IntPtr list, IntPtr attribute, IntPtr value, int size)
    {
        if (!UpdateProcThreadAttribute(list, 0, attribute, value, size, IntPtr.Zero, IntPtr.Zero)) throw Win32("UpdateProcThreadAttribute");
    }

    /// <summary>
    /// A minimal, sorted environment: where the runtime lives, plus the variables Windows needs to
    /// set up the container's own storage. Nothing else from the host process is passed on.
    /// </summary>
    private static string EnvironmentBlock()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static Win32Exception Win32(string call)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{call} failed while starting the sandboxed worker: {new Win32Exception(error).Message} ({error}).");
    }

    private sealed class SandboxedWorker(SafeFileHandle job, SafeProcessHandle process, Stream fromWorker, Stream toWorker, Action<ulong> peakMemory)
        : IWorkerProcess
    {
        private const uint StillActive = 259;

        public Stream FromWorker => fromWorker;
        public Stream ToWorker => toWorker;

        public void Kill() => TerminateJobObject(job, 1);

        public long ShareReadOnly(SafeFileHandle file)
        {
            // The duplicate lives in the worker's handle table with read access only; the worker cannot
            // widen it, and it closes when the worker exits.
            if (!DuplicateHandle(GetCurrentProcess(), file, process, out var remote, FILE_GENERIC_READ, false, 0))
                throw Win32("DuplicateHandle");
            return remote.ToInt64();
        }

        public string? DescribeExit()
        {
            WaitForSingleObject(process, 2000);
            return GetExitCodeProcess(process, out var code) && code != StillActive ? $"exit code 0x{code:X8}" : null;
        }

        public void Dispose()
        {
            Kill();
            unsafe
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
                if (QueryInformationJobObject(job, JobObjectExtendedLimitInformation, (IntPtr)(&info), sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), IntPtr.Zero))
                    peakMemory((ulong)info.PeakProcessMemoryUsed);
            }
            job.Dispose();
            process.Dispose();
            toWorker.Dispose();
            fromWorker.Dispose();
        }
    }
}
