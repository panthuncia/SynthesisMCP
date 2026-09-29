using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using SafePatch.Host;
using SafePatch.Mutagen;
using SafePatch.Sandbox.Windows;

namespace SafePatch.Synthesis;

/// <summary>
/// Called from a generated patcher's Program.cs. Runs the packaged program in the sandbox through
/// the real Synthesis pipeline, then validates what it produced and applies it to
/// <c>state.PatchMod</c>. Throws on any failure so Synthesis stops.
/// </summary>
public static class SafePatchHost
{
    public const string ManifestFile = "manifest.json";
    public const string ProgramFile = "patch.bin";

    /// <summary>
    /// The sandboxed worker, in its own folder next to the patcher (see <c>buildTransitive/SafePatch.Synthesis.targets</c>).
    /// That folder is the only one the AppContainer is granted.
    /// </summary>
    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "SafePatch.Worker", "SafePatch.Worker.exe");

    /// <param name="arguments">The arguments Synthesis passed to this patcher's Main.</param>
    /// <param name="packageDirectory">Folder holding manifest.json and patch.bin, relative to the patcher's output.</param>
    public static SessionReport Run(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, IReadOnlyList<string> arguments, string packageDirectory = "payload")
    {
        if (!OperatingSystem.IsWindows()) throw new SafePatchException("SafePatch requires Windows for its worker sandbox.");

        var directory = Path.Combine(AppContext.BaseDirectory, packageDirectory);
        var package = Preflight.Verify(
            File.ReadAllText(Path.Combine(directory, ManifestFile)),
            File.ReadAllBytes(Path.Combine(directory, ProgramFile)),
            PatchPolicy.PublisherDefault);

        var launcher = new AppContainerLauncher(new SandboxOptions { WorkerPath = WorkerPath });
        var run = SynthesisInputs.Parse(arguments);
        var committer = new MutagenPatchCommitter<ISkyrimMod, ISkyrimModGetter>(
            state.PatchMod, state.LinkCache, state.GameRelease, FormKeyPersistence.FromArguments(run.PersistencePath, run.PatcherName),
            SynthesisInputs.Format(run, state.LoadOrder.ListedOrder.Select(l => l.ModKey)));

        try
        {
            var report = new PatchSession(launcher, committer).Run(package, SynthesisInputs.Plan(arguments, package.Manifest.Settings?.Path), state.Cancel);
            Console.WriteLine(report.Log);
            Console.WriteLine($"SafePatch '{report.PackageName}': {report.Changes.Count} record change(s).");
            Console.WriteLine(report.ToJson());
            return report;
        }
        catch (PatchRejectedException e) when (e.Message.Contains("expecting Hello", StringComparison.Ordinal) && RunningUnderMo2())
        {
            throw new PatchRejectedException(
                e.Message + " Mod Organizer 2 injects its virtual file system into every process started under it, and the " +
                "sandboxed worker cannot start with it. The worker needs no VFS (SafePatch opens every file for it): add " +
                "SafePatch.Worker.exe to MO2's executables blacklist (Settings > Workarounds > Executables Blacklist).",
                e.Log, e);
        }
        catch (PatchRejectedException e)
        {
            if (e.Log is { Length: > 0 } log) Console.Error.WriteLine(log);
            throw;
        }
    }

    /// <summary>Whether MO2's usvfs is loaded into this process, as Synthesis itself checks.</summary>
    private static bool RunningUnderMo2() =>
        System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Any(m => m.ModuleName.StartsWith("usvfs_", StringComparison.OrdinalIgnoreCase));
}
