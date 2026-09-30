using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// A native patch function, run by the real Synthesis pipeline inside the worker over brokered
/// plugin files, validated and committed by the host. Everything but the OS sandbox.
/// </summary>
public class VerticalSliceTests
{
    [Fact]
    public void Native_merge_program_restores_the_dropped_entry_and_keeps_unexposed_data()
    {
        using var run = new HostRun();

        var report = run.Run(SamplePrograms.LeveledListMerge);

        var change = Assert.Single(report.Changes);
        Assert.Equal("LeveledItem", change.RecordType);
        Assert.False(change.IsNew);
        Assert.Equal(["Entries"], change.Fields);

        var list = Assert.Single(run.PatchMod.LeveledItems);
        Assert.Equal(
            [run.Plugins.Sword, run.Plugins.Potion, run.Plugins.OtherArmor, run.Plugins.CacoPotion],
            list.Entries!.Select(e => e.Data!.Reference.FormKey));
        // The sword's owner was never touched by the program, and survives the round trip. Mutagen
        // reads an owner from another plugin as untyped, so compare the FormKey it points at.
        var owner = list.Entries![0].ExtraData!.Owner switch
        {
            FactionOwner f => f.Faction.FormKey,
            UntypedOwner u => u.OwnerData.FormKey,
            var other => throw new InvalidOperationException($"unexpected owner {other}"),
        };
        Assert.Equal(run.Plugins.Owner, owner);
    }

    [Fact]
    public void Two_runs_on_the_same_inputs_report_and_write_the_same_thing()
    {
        var package = TestPrograms.Package(TestPrograms.Compile(SamplePrograms.LeveledListMerge));
        (string Report, byte[] Patch) RunOnce()
        {
            using var run = new HostRun();
            var report = new PatchSession(InProcessWorkerLauncher.Real(), run.Committer()).Run(package, run.Inputs(), TestContext.Current.CancellationToken);
            var path = Path.Combine(run.Root, "Result", "Synthesis.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            run.PatchMod.WriteToBinary(path);
            return (report.ToJson(), File.ReadAllBytes(path));
        }

        var first = RunOnce();
        var second = RunOnce();

        Assert.Equal(first.Report, second.Report);
        Assert.Equal(first.Patch, second.Patch);
    }

    [Fact]
    public void Rerunning_over_its_own_output_changes_nothing()
    {
        using var folder = new TempFolder("SafePatchPrevious-");
        var previous = folder.File("Synthesis.esp");
        using (var first = new HostRun())
        {
            first.Run(SamplePrograms.LeveledListMerge);
            first.PatchMod.WriteToBinary(previous);
        }

        using var second = new HostRun(previousPatchSource: previous);
        var report = second.Run(SamplePrograms.LeveledListMerge);

        Assert.Empty(report.Changes);
        Assert.Single(second.PatchMod.LeveledItems);
    }

    [Fact]
    public void A_field_outside_the_manifest_is_rejected_and_nothing_is_applied()
    {
        using var run = new HostRun();
        var renamer = SamplePrograms.LeveledListMerge.Replace(
            "var patched = winner.GetOrAddAsOverride(state.PatchMod);",
            "var patched = winner.GetOrAddAsOverride(state.PatchMod); patched.EditorID = \"Renamed\";");

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(renamer));

        Assert.Contains("changing LeveledItem.EditorID is not allowed", e.Message);
        Assert.Empty(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void A_change_inside_a_sub_object_is_seen()
    {
        // Equals masks expose sub-object entries (here Weapon.Data) as properties rather than fields.
        using var run = new HostRun();
        var e = Assert.Throws<PatchRejectedException>(() => run.Run(Program("""
            var sword = state.LoadOrder.PriorityOrder.Weapon().WinningOverrides().First();
            state.PatchMod.Weapons.GetOrAddAsOverride(sword).Data = new WeaponData { Speed = 9 };
            """)));

        Assert.Contains("changing Weapon.Data is not allowed", e.Message);
    }

    [Fact]
    public void A_throwing_program_is_rejected_with_its_log()
    {
        using var run = new HostRun();
        const string thrower = """
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class P
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    System.Console.WriteLine("about to fail");
                    throw new System.InvalidOperationException("halfway");
                }
            }
            """;

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(thrower));

        Assert.Contains("failed", e.Message);
        Assert.Empty(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void Creating_records_requires_the_type_to_be_creatable()
    {
        const string creator = """
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class P
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) =>
                    state.PatchMod.LeveledItems.AddNew("LItemBrandNew");
            }
            """;

        using (var denied = new HostRun())
        {
            var e = Assert.Throws<PatchRejectedException>(() => denied.Run(creator));
            Assert.Contains("creating LeveledItem records is not allowed", e.Message);
        }

        using var allowed = new HostRun();
        var report = allowed.Run(creator, creatable: ["LeveledItem"]);
        Assert.True(Assert.Single(report.Changes).IsNew);
        Assert.Equal("LItemBrandNew", Assert.Single(allowed.PatchMod.LeveledItems).EditorID);
    }

    private static string Program(string body) => $$"""
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class P
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public void A_new_link_to_a_missing_record_is_rejected()
    {
        using var run = new HostRun();
        var e = Assert.Throws<PatchRejectedException>(() => run.Run(Program("""
            var list = state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides().First().GetOrAddAsOverride(state.PatchMod);
            list.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(FormKey.Factory("000ABC:Skyrim.esm")) } });
            """)));

        Assert.Contains("links to missing record 000ABC:Skyrim.esm", e.Message);
    }

    [Fact]
    public void A_link_to_a_form_the_engine_defines_is_not_a_missing_record()
    {
        // PlayerRef (000014:Skyrim.esm) is hardcoded by the engine: no plugin has it, yet conditions name it everywhere.
        using var run = new HostRun();
        var report = run.Run(Program("""
            var list = state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides().First().GetOrAddAsOverride(state.PatchMod);
            list.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(FormKey.Factory("000014:Skyrim.esm")) } });
            """));

        Assert.Single(report.Changes);
    }

    [Fact]
    public void Overriding_a_record_outside_the_load_order_is_rejected()
    {
        using var run = new HostRun();
        var e = Assert.Throws<PatchRejectedException>(() => run.Run(Program("""
            state.PatchMod.LeveledItems.Add(new LeveledItem(FormKey.Factory("000ABC:Missing.esp"), SkyrimRelease.SkyrimSE) { EditorID = "Ghost" });
            """), creatable: ["LeveledItem"]));

        Assert.Contains("overrides a record that is not in the load order", e.Message);
    }

    [Fact]
    public void Exceeding_the_record_limit_is_rejected()
    {
        using var run = new HostRun();
        var policy = PatchPolicy.PublisherDefault with { MaxRecords = 2 };
        var package = TestPrograms.Package(TestPrograms.Compile(Program("""
            for (var i = 0; i < 3; i++) state.PatchMod.LeveledItems.AddNew($"LItemNew{i}");
            """)), creatable: ["LeveledItem"], policy: policy);

        var e = Assert.Throws<PatchRejectedException>(() => new PatchSession(InProcessWorkerLauncher.Real(), run.Committer())
            .Run(package, run.Inputs(), TestContext.Current.CancellationToken));

        Assert.Contains("more than the limit of 2", e.Message);
    }
}
