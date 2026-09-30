using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using SafePatch.Host;
using static SafePatch.Sandbox.Windows.Native;

namespace SafePatch.Sandbox.Windows;

/// <summary>
/// Mod Organizer 2's virtual file system (usvfs), seen from a process MO2 started. MO2 injects usvfs into every
/// process it starts and into their children, and redirects their file access into the profile's mod folders.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Mo2Vfs
{
    /// <summary>The library MO2 injects into 64-bit processes.</summary>
    public const string Module = "usvfs_x64.dll";

    /// <summary>Whether this process runs inside MO2's virtual file system.</summary>
    public static bool Active => GetModuleHandle(Module) != IntPtr.Zero;

    /// <summary>
    /// The file a path really opens: inside MO2's VFS, the mod folder's copy of a virtual Data file. Outside it, the
    /// path itself (links resolved). Null when the file does not exist.
    /// </summary>
    public static string? RealPath(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
        using (handle)
        {
            var buffer = new char[1024];
            while (true)
            {
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not resolve {path}.");
                if (length < buffer.Length) return StripPrefix(new string(buffer, 0, (int)length));
                buffer = new char[length];
            }
        }
    }

    private static string StripPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>The process at the other end of a connected pipe.</summary>
    public static int ClientProcessId(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed.");

    /// <summary>Closes the console window MO2 gives a console program it starts, for a program that talks through a pipe instead.</summary>
    public static void DetachConsole() => FreeConsole();
}
