using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// Removing records from the patch mod: reverting an earlier patcher's override, or deleting a record
/// it added. Allowed only for the manifest's <c>Removable</c> types, and never leaving a dangling link.
/// </summary>
public sealed class RemovalTests : IDisposable
{
    private static string Program(string body) => $$"""
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        using Noggog;
        public static class P
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                {{body}}
            }
        }
        """;

    private const string AddLinkedPotion = """
        var potion = state.PatchMod.Ingestibles.AddNew("NewPotion");
        var list = state.PatchMod.LeveledItems.AddNew("NewList");
        list.Entries = [new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(potion.FormKey) } }];
        """;

    private readonly List<string> _folders = [];

    /// <summary>Runs a first patcher and returns its output, as Synthesis hands it to the next patcher.</summary>
    private string Previous(string body, IReadOnlyList<string>? writable = null, IReadOnlyList<string>? creatable = null)
    {
        using var first = new HostRun();
        first.Run(Program(body), writable ?? [], creatable ?? []);
        var folder = Directory.CreateTempSubdirectory("SafePatchPrevious-").FullName;
        _folders.Add(folder);
        var path = Path.Combine(folder, "Synthesis.esp");
        first.PatchMod.WriteToBinary(path);
        return path;
    }

    private string PreviousMerge()
    {
        using var first = new HostRun();
        first.Run(SamplePrograms.LeveledListMerge);
        var folder = Directory.CreateTempSubdirectory("SafePatchPrevious-").FullName;
        _folders.Add(folder);
        first.PatchMod.WriteToBinary(Path.Combine(folder, "Synthesis.esp"));
        return Path.Combine(folder, "Synthesis.esp");
    }

    [Fact]
    public void Reverting_an_override_is_rejected_when_its_type_is_not_removable()
    {
        using var run = new HostRun(previousPatchSource: PreviousMerge());

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(Program("state.PatchMod.LeveledItems.Clear();"), removable: ["Ingestible"]));

        Assert.Contains("removing LeveledItem records is not allowed", e.Message);
        Assert.Single(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void Reverting_an_override_is_reported_and_applied_when_its_type_is_removable()
    {
        using var run = new HostRun(previousPatchSource: PreviousMerge());

        var report = run.Run(Program("state.PatchMod.LeveledItems.Clear();"), removable: ["LeveledItem"]);

        var change = Assert.Single(report.Changes);
        Assert.Equal((run.Plugins.List.ToString(), "LeveledItem", true, false), (change.FormKey, change.RecordType, change.IsRemoved, change.IsNew));
        Assert.Empty(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void Deleting_a_record_the_patch_still_links_to_is_rejected()
    {
        using var run = new HostRun(previousPatchSource: Previous(AddLinkedPotion, creatable: ["Ingestible", "LeveledItem"]));

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(Program("state.PatchMod.Ingestibles.Clear();"), removable: ["Ingestible"]));

        Assert.Contains("NewList", e.Message);
        Assert.Contains("links to removed record", e.Message);
        Assert.Single(run.PatchMod.Ingestibles);
    }

    [Fact]
    public void Deleting_a_record_together_with_everything_linking_to_it_is_accepted()
    {
        using var run = new HostRun(previousPatchSource: Previous(AddLinkedPotion, creatable: ["Ingestible", "LeveledItem"]));

        var report = run.Run(Program("state.PatchMod.Ingestibles.Clear(); state.PatchMod.LeveledItems.Clear();"), removable: ["Ingestible", "LeveledItem"]);

        Assert.Equal(2, report.Changes.Count(c => c.IsRemoved));
        Assert.Empty(run.PatchMod.Ingestibles);
        Assert.Empty(run.PatchMod.LeveledItems);
    }

    [Fact]
    public void Removing_a_nested_reference_leaves_its_cell()
    {
        var reference = new HostRun();
        var (interiorRef, interiorCell) = (reference.Plugins.InteriorRef, reference.Plugins.InteriorCell);
        reference.Dispose();
        var previous = Previous($"""
            var placed = state.LinkCache.ResolveContext<IPlacedObject, IPlacedObjectGetter>(FormKey.Factory("{interiorRef}")).GetOrAddAsOverride(state.PatchMod);
            placed.Placement = new Placement {"{"} Position = new P3Float(1, 2, 3) {"}"};
            """, writable: ["PlacedObject.Placement"]);
        using var run = new HostRun(previousPatchSource: previous);

        var report = run.Run(Program($"""state.PatchMod.Remove<IPlacedObjectGetter>(FormKey.Factory("{interiorRef}"));"""), removable: ["PlacedObject"]);

        var change = Assert.Single(report.Changes);
        Assert.Equal(("PlacedObject", true), (change.RecordType, change.IsRemoved));
        Assert.Empty(run.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>());
        Assert.Equal(interiorCell, Assert.Single(run.PatchMod.EnumerateMajorRecords<ICellGetter>()).FormKey);
    }

    [Theory]
    [InlineData(new[] { "Cell" }, false)]
    [InlineData(new[] { "Cell", "PlacedObject" }, true)]
    public void Removing_a_cell_removes_its_references_and_each_must_be_removable(string[] removable, bool accepted)
    {
        var reference = new HostRun();
        var (interiorRef, interiorCell) = (reference.Plugins.InteriorRef, reference.Plugins.InteriorCell);
        reference.Dispose();
        var previous = Previous($"""
            var placed = state.LinkCache.ResolveContext<IPlacedObject, IPlacedObjectGetter>(FormKey.Factory("{interiorRef}")).GetOrAddAsOverride(state.PatchMod);
            placed.Placement = new Placement {"{"} Position = new P3Float(1, 2, 3) {"}"};
            """, writable: ["PlacedObject.Placement"]);
        using var run = new HostRun(previousPatchSource: previous);
        var program = Program($"""state.PatchMod.Remove<ICellGetter>(FormKey.Factory("{interiorCell}"));""");

        if (!accepted)
        {
            var e = Assert.Throws<PatchRejectedException>(() => run.Run(program, removable: removable));
            Assert.Contains("removing PlacedObject records is not allowed", e.Message);
            Assert.Single(run.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>());
            return;
        }

        var report = run.Run(program, removable: removable);
        Assert.Equal(["Cell", "PlacedObject"], report.Changes.Where(c => c.IsRemoved).Select(c => c.RecordType).Order());
        Assert.Empty(run.PatchMod.EnumerateMajorRecords<ICellGetter>());
        Assert.Empty(run.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>());
    }

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
