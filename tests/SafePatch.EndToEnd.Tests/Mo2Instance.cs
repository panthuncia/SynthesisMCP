using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SafePatch.EndToEnd.Tests;

/// <summary>
/// A throwaway portable Mod Organizer 2 instance managing the workspace's fake Skyrim SE install.
/// MO2 keeps a portable instance next to its executable, so the pinned install is mirrored with
/// hard links (no copying, and the shared install is never modified).
/// </summary>
public sealed partial class Mo2Instance
{
    public const string ProfileName = "Default";

    private readonly E2ETools _tools;
    private readonly Workspace _workspace;

    public Mo2Instance(E2ETools tools, Workspace workspace)
    {
        _tools = tools;
        _workspace = workspace;
        MirrorInstall(Path.GetDirectoryName(tools.ModOrganizer)!, Directory);

        // portable.txt locks MO2 to this portable instance: it never reads the user's global instances.
        File.WriteAllText(Path.Combine(Directory, "portable.txt"), "");
        File.WriteAllText(Path.Combine(Directory, "ModOrganizer.ini"), $"""
            [General]
            gameName=Skyrim Special Edition
            game_edition=Steam
            gamePath=@ByteArray({workspace.GameFolder.Replace('\\', '/')})
            selected_profile=@ByteArray({ProfileName})
            first_start=false

            [Settings]
            check_for_updates=false
            """);
        foreach (var folder in new[] { "mods", "overwrite", "downloads", Path.Combine("profiles", ProfileName) })
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, folder));
        File.WriteAllText(Path.Combine(ProfileFolder, "archives.txt"), "");
        File.WriteAllText(Path.Combine(ProfileFolder, "settings.ini"), "[General]\nLocalSaves=false\nLocalSettings=false\n");
    }

    public string Directory => Path.Combine(_workspace.Root, "MO2");
    public string ProfileFolder => Path.Combine(Directory, "profiles", ProfileName);
    public string PluginsFile => Path.Combine(ProfileFolder, "plugins.txt");

    /// <summary>A mod folder MO2 overlays onto the game's Data folder while it runs a program.</summary>
    public string ModFolder(string name) => Path.Combine(Directory, "mods", name);

    /// <summary>Enables the given mods, highest priority last, and lists the plugins in load order.</summary>
    public void SetProfile(IReadOnlyList<string> mods, IReadOnlyList<string> plugins)
    {
        // modlist.txt lists the highest priority mod first.
        File.WriteAllLines(Path.Combine(ProfileFolder, "modlist.txt"), mods.Reverse().Select(m => "+" + m));
        File.WriteAllLines(PluginsFile, plugins.Select(p => "*" + p));
        File.WriteAllLines(Path.Combine(ProfileFolder, "loadorder.txt"), [Workspace.BaseMaster, .. plugins]);
    }

    /// <summary>
    /// Runs a program inside MO2's virtual file system and returns its exit code and output. MO2's
    /// own exit code only says whether it started the program, so a script records both.
    /// </summary>
    public ProcessResult Run(string exe, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var log = Path.Combine(_workspace.Root, "mo2-run.log");
        var exitCode = Path.Combine(_workspace.Root, "mo2-run.exitcode");
        var script = Path.Combine(_workspace.Root, "mo2-run.cmd");
        File.WriteAllText(script, $"""
            @echo off
            cd /d {Quote(_workspace.Root)}
            {Quote(exe)} {string.Join(" ", arguments.Select(Quote))} > {Quote(log)} 2>&1
            echo %ERRORLEVEL% > {Quote(exitCode)}
            """);

        // --multiple only bypasses MO2's single-process lock; RequireNoOtherVfs is what keeps us isolated.
        var mo2 = Processes.Run(Path.Combine(Directory, "ModOrganizer.exe"),
            ["--multiple", "-p", ProfileName, "run", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "-a", $"/c \"{script}\""],
            Directory, timeout);

        var output = $"--- ModOrganizer ({mo2.ExitCode}) ---\n{mo2.Output}\n--- program ---\n"
                     + (File.Exists(log) ? File.ReadAllText(log) : "<no output>")
                     + $"\n--- MO2 log ---\n{Mo2Log()}";
        return File.Exists(exitCode) && int.TryParse(File.ReadAllText(exitCode).Trim(), out var code)
            ? new ProcessResult(code, output)
            : new ProcessResult(int.MinValue, output);
    }

    /// <summary>
    /// Skips the test if any other MO2 virtual file system is live in this session. MO2 2.5.2 names
    /// its usvfs shared memory with a fixed ID ("mod_organizer_instance", usvfsconnector.cpp), so a
    /// second MO2, even with --multiple, joins the running one's VFS and rewrites its shared settings.
    /// That would tamper with the developer's real modding setup, so we never start alongside one.
    /// </summary>
    public static void RequireNoOtherVfs()
    {
        var users = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase) || LoadsUsvfs(p))
            .Select(p => $"{p.ProcessName} ({p.Id})")
            .ToList();
        Assert.SkipWhen(users.Count > 0,
            $"Another Mod Organizer VFS is running ({string.Join(", ", users)}). Close Mod Organizer and anything launched from it; " +
            "MO2 shares one usvfs instance per session, so this test would join and disturb it.");
    }

    private static bool LoadsUsvfs(System.Diagnostics.Process process)
    {
        try
        {
            return process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Any(m => m.ModuleName.StartsWith("usvfs_", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false; // Exited, or not inspectable (elevated/protected); ModOrganizer itself is caught by name.
        }
    }

    private string Mo2Log()
    {
        var path = Path.Combine(Directory, "logs", "mo_interface.log");
        if (!File.Exists(path)) return "<none>";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var lines = new StreamReader(stream).ReadToEnd().Split('\n');
        return string.Join('\n', lines.TakeLast(60));
    }

    private static string Quote(string argument) => $"\"{argument}\"";

    private static void MirrorInstall(string source, string destination)
    {
        foreach (var directory in System.IO.Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            System.IO.Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == ".installed") continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (!CreateHardLink(target, file, IntPtr.Zero))
            {
                // Different volume: fall back to copying.
                if (Marshal.GetLastWin32Error() != 17 /* ERROR_NOT_SAME_DEVICE */) throw new Win32Exception();
                File.Copy(file, target);
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
