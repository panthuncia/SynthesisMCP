using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.Synthesis;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// Synthesis options that change how plugins are written: localization, embedded string encoding
/// and splitting a plugin that needs too many masters. The worker's plugin is an intermediate, so
/// these must never change what the host accepts.
/// </summary>
public class PluginFormatTests
{
    private const string RenameWeapons = """
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class Renamer
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
                {
                    if (weapon.EditorID is { } id && id.StartsWith("WeaponFiller"))
                        state.PatchMod.Weapons.GetOrAddAsOverride(weapon).EditorID = id + "X";
                }
            }
        }
        """;

    [Fact]
    public void Localize_is_left_to_the_host_and_changes_nothing_accepted()
    {
        using var run = new HostRun(extraArguments: ["--Localize"]);

        Assert.DoesNotContain("--Localize", run.Inputs().Arguments);
        var change = Assert.Single(run.Run(SamplePrograms.LeveledListMerge).Changes);
        Assert.Equal(["Entries"], change.Fields);
    }

    [Fact]
    public void A_localized_source_is_read_through_its_strings_files()
    {
        var previous = Path.Combine(Directory.CreateTempSubdirectory("SafePatchLocalized-").FullName, "Synthesis.esp");
        var source = new SkyrimMod(ModKey.FromFileName("Synthesis.esp"), SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        source.Weapons.AddNew("LocalizedSword").Name = "Localized Sword";
        source.WriteToBinary(previous);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(previous)!, "Strings")));
        const string program = """
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Seer
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    foreach (var weapon in state.PatchMod.Weapons) weapon.EditorID = "Seen" + weapon.Name?.String;
                }
            }
            """;
        try
        {
            using var run = new HostRun(previousPatchSource: previous, extraArguments: ["--Localize"]);

            var change = Assert.Single(run.Run(program, writable: ["Weapon.EditorID"]).Changes);

            Assert.Equal(["EditorID"], change.Fields);
            var weapon = Assert.Single(run.PatchMod.Weapons);
            Assert.Equal("SeenLocalized Sword", weapon.EditorID);
            Assert.Equal("Localized Sword", weapon.Name?.String);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(previous)!, recursive: true);
        }
    }

    [Fact]
    public void Utf8_embedded_strings_are_read_back_as_written()
    {
        using var run = new HostRun(extraArguments: ["--UseUtf8ForEmbeddedStrings"]);
        const string program = """
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Namer
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) =>
                    state.PatchMod.Weapons.AddNew("Utf8Sword").Name = "Épée ☃ 剣";
            }
            """;

        run.Run(program, creatable: ["Weapon"]);

        Assert.Equal("Épée ☃ 剣", Assert.Single(run.PatchMod.Weapons).Name?.String);
    }

    [Fact]
    public void A_patch_split_for_too_many_masters_is_read_back_whole()
    {
        using var run = new HostRun(extraArguments: ["--SplitIfMaxMastersExceeded"], fillerPlugins: 300);
        var parts = new PartCounter(run.Committer());

        var report = new PatchSession(InProcessWorkerLauncher.Real(), parts).Run(
            TestPrograms.Package(TestPrograms.Compile(RenameWeapons), writable: ["Weapon.EditorID"]), run.Inputs(), TestContext.Current.CancellationToken);

        Assert.True(parts.Parts > 1, $"The worker wrote {parts.Parts} part(s).");
        Assert.Equal(300, report.Changes.Count);
        Assert.All(report.Changes, c => Assert.Equal(["EditorID"], c.Fields));
        Assert.Equal(300, run.PatchMod.Weapons.Count);
        Assert.All(run.PatchMod.Weapons, w => Assert.EndsWith("X", w.EditorID));
    }

    [Fact]
    public void Without_splitting_a_patch_needing_too_many_masters_fails()
    {
        using var run = new HostRun(fillerPlugins: 300);

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(RenameWeapons, writable: ["Weapon.EditorID"]));

        // Synthesis prints the TooManyMastersException to the program's log and exits with an error.
        Assert.Contains("Patch program failed", e.Message);
        Assert.Empty(run.PatchMod.Weapons);
    }

    [Fact]
    public void Split_parts_are_rejected_when_the_run_does_not_split()
    {
        using var run = new HostRun();
        var recorder = new RecordingCommitter();
        new PatchSession(InProcessWorkerLauncher.Real(), recorder).Run(
            TestPrograms.Package(TestPrograms.Compile(SamplePrograms.LeveledListMerge)), run.Inputs(), TestContext.Current.CancellationToken);

        var e = Assert.Throws<PatchRejectedException>(() => run.Committer().Commit(
            [recorder.OutputPlugin!, recorder.OutputPlugin!], null, TestPrograms.Package([0x4D, 0x5A]).Policy));

        Assert.Contains("does not split", e.Message);
        Assert.Empty(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void The_worker_keeps_every_argument_but_localize()
    {
        string[] arguments = ["run-patcher", "--GameRelease", "SkyrimSE", "--DataFolderPath", "D", "--OutputPath", "O.esp",
            "--Localize", "--TargetLanguage", "French", "--UseUtf8ForEmbeddedStrings", "--SplitIfMaxMastersExceeded"];

        var worker = SynthesisInputs.WorkerArguments(arguments);

        Assert.Equal(arguments.Where(a => a != "--Localize"), worker);
        var parsed = SynthesisInputs.Parse(worker);
        Assert.False(parsed.Localize);
        Assert.True(parsed.SplitIfMaxMastersExceeded);
    }

    /// <summary>Counts the parts the worker returned, then commits them.</summary>
    private sealed class PartCounter(IPatchCommitter inner) : IPatchCommitter
    {
        public int Parts { get; private set; }

        public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy)
        {
            Parts = outputPlugins.Count;
            return inner.Commit(outputPlugins, persistence, policy);
        }
    }
}
