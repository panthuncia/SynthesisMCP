using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// Synthesis's FormKey persistence keeps new records' FormKeys stable across runs. The worker's
/// allocator runs natively; the host checks what it wrote before writing it for real.
/// </summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _persistence = Path.Combine(Directory.CreateTempSubdirectory("SafePatchPersistence-").FullName, "Persistence");

    private static string Create(params string[] editorIds) => $$"""
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class P
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                {{string.Concat(editorIds.Select(e => $"state.PatchMod.LeveledItems.AddNew(\"{e}\");"))}}
            }
        }
        """;

    private string OwnFile => Path.Combine(_persistence, "SafePatchTest.txt");

    [Fact]
    public void A_new_record_keeps_its_FormKey_across_runs()
    {
        FormKey first;
        using (var run = new HostRun(persistenceFolder: _persistence))
        {
            run.Run(Create("LItemPersisted"), creatable: ["LeveledItem"]);
            first = Assert.Single(run.PatchMod.LeveledItems).FormKey;
        }
        Assert.Equal(["LItemPersisted", first.ID.ToString()], File.ReadAllLines(OwnFile));

        // Another record is created first this time; without persistence it would take the first ID.
        using var again = new HostRun(persistenceFolder: _persistence);
        again.Run(Create("LItemNewcomer", "LItemPersisted"), creatable: ["LeveledItem"]);

        Assert.Equal(first, again.PatchMod.LeveledItems.Single(l => l.EditorID == "LItemPersisted").FormKey);
        Assert.NotEqual(first, again.PatchMod.LeveledItems.Single(l => l.EditorID == "LItemNewcomer").FormKey);
        Assert.Equal(4, File.ReadAllLines(OwnFile).Length);
    }

    [Fact]
    public void New_records_raise_the_next_FormID_even_without_persistence()
    {
        using var run = new HostRun();

        run.Run(Create("LItemA", "LItemB"), creatable: ["LeveledItem"]);

        Assert.True(((IMod)run.PatchMod).NextFormID > run.PatchMod.LeveledItems.Max(l => l.FormKey.ID));
    }

    /// <summary>Runs the program, then commits its real output with a persistence file the test supplies.</summary>
    private static PatchRejectedException CommitWith(HostRun run, string persistence)
    {
        var recorder = new RecordingCommitter();
        new PatchSession(InProcessWorkerLauncher.Real(), recorder).Run(
            TestPrograms.Package(TestPrograms.Compile(Create("LItemPersisted")), creatable: ["LeveledItem"]), run.Inputs(), TestContext.Current.CancellationToken);

        return Assert.Throws<PatchRejectedException>(() => run.Committer().Commit(
            recorder.OutputPlugins!, Encoding.UTF8.GetBytes(persistence), TestPrograms.Package([0x4D, 0x5A], creatable: ["LeveledItem"]).Policy));
    }

    [Fact]
    public void A_persistence_entry_that_names_no_new_record_is_rejected_and_nothing_is_applied()
    {
        using var run = new HostRun(persistenceFolder: _persistence);

        var e = CommitWith(run, "LItemSomethingElse\n2048\n");

        Assert.Contains("names no new record", e.Message);
        Assert.Empty(run.PatchMod.LeveledItems);
        Assert.False(File.Exists(OwnFile));
    }

    [Fact]
    public void A_persistence_entry_another_patcher_owns_is_rejected()
    {
        using var run = new HostRun(persistenceFolder: _persistence);
        File.WriteAllLines(Path.Combine(_persistence, "OtherPatcher.txt"), ["LItemTheirs", "3000"]);

        var e = CommitWith(run, "LItemTheirs\n3000\n");

        Assert.Contains("collides with another patcher's", e.Message);
    }

    [Fact]
    public void Persistence_is_rejected_when_the_run_does_not_use_it()
    {
        using var run = new HostRun();

        var e = CommitWith(run, "LItemPersisted\n2048\n");

        Assert.Contains("does not use it", e.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_persistence)!, recursive: true); } catch (IOException) { }
    }
}
