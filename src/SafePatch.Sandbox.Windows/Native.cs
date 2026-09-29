using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SafePatch.Sandbox.Windows;

#pragma warning disable SYSLIB1054 // DllImport keeps the struct marshalling here plain and reviewable.
internal static class Native
{
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint DETACHED_PROCESS = 0x00000008;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY = 0x0002000E;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY = 0x0002000F;
    public const uint PROCESS_CREATION_CHILD_PROCESS_RESTRICTED = 0x01;
    public const uint PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT = 0x01;

    public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const uint JOB_OBJECT_UILIMIT_ALL = 0x000000FF;
    public const int JobObjectBasicUIRestrictions = 4;
    public const int JobObjectExtendedLimitInformation = 9;

    public const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    public static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    public static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string? applicationName, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    public const uint FILE_GENERIC_READ = 0x00120089;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle sourceHandle, SafeProcessHandle targetProcess,
        out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);
}
#pragma warning restore SYSLIB1054
