using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// Records that live inside other records (cell references, dialog responses) are validated as
/// records of their own, and their parents without their children.
/// </summary>
public class NestedRecordTests
{
    private static string Program(string body) => $$"""
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Plugins.Cache;
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

    private static string MoveRef(FormKey reference) => Program($"""
        var placed = state.LinkCache.ResolveContext<IPlacedObject, IPlacedObjectGetter>(FormKey.Factory("{reference}")).GetOrAddAsOverride(state.PatchMod);
        placed.Placement = new Placement {"{"} Position = new P3Float(1, 2, 3) {"}"};
        """);

    private static IPlacedObjectGetter Placed(HostRun run, FormKey key) =>
        run.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>().Single(p => p.FormKey == key);

    [Fact]
    public void Moving_an_interior_reference_changes_only_the_reference()
    {
        using var run = new HostRun();

        var report = run.Run(MoveRef(run.Plugins.InteriorRef), writable: ["PlacedObject.Placement"]);

        var change = Assert.Single(report.Changes);
        Assert.Equal(("PlacedObject", false), (change.RecordType, change.IsNew));
        Assert.Equal(["Placement"], change.Fields);
        Assert.Equal(new Noggog.P3Float(1, 2, 3), Placed(run, run.Plugins.InteriorRef).Placement!.Position);
        // The parent cell came along as a container, without its other contents.
        var cell = run.PatchMod.EnumerateMajorRecords<ICellGetter>().Single();
        Assert.Equal(run.Plugins.InteriorCell, cell.FormKey);
    }

    [Fact]
    public void A_reference_field_outside_the_manifest_is_rejected()
    {
        using var run = new HostRun();

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(MoveRef(run.Plugins.InteriorRef), writable: ["PlacedObject.Scale"]));

        Assert.Contains("changing PlacedObject.Placement is not allowed", e.Message);
        Assert.Empty(run.PatchMod.EnumerateMajorRecords());
    }

    [Fact]
    public void Exterior_and_persistent_references_are_patched_inside_their_worldspace()
    {
        using var run = new HostRun();
        var both = Program($"""
            foreach (var key in new[] {"{"} "{run.Plugins.ExteriorRef}", "{run.Plugins.PersistentRef}" {"}"})
                state.LinkCache.ResolveContext<IPlacedObject, IPlacedObjectGetter>(FormKey.Factory(key)).GetOrAddAsOverride(state.PatchMod)
                    .Placement = new Placement {"{"} Position = new P3Float(4, 5, 6) {"}"};
            """);

        var report = run.Run(both, writable: ["PlacedObject.Placement"]);

        Assert.Equal(2, report.Changes.Count);
        Assert.All(report.Changes, c => Assert.Equal("PlacedObject", c.RecordType));
        var worldspace = Assert.Single(run.PatchMod.Worldspaces);
        Assert.Equal(run.Plugins.PersistentRef, Assert.Single(worldspace.TopCell!.Persistent).FormKey);
        var exterior = worldspace.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Single();
        Assert.Equal(run.Plugins.ExteriorRef, Assert.Single(exterior.Temporary).FormKey);
        Assert.Equal(new Noggog.P3Float(4, 5, 6), Placed(run, run.Plugins.ExteriorRef).Placement!.Position);
    }

    [Fact]
    public void Adding_a_reference_to_an_existing_cell_requires_it_to_be_creatable()
    {
        var add = (HostRun run) => Program($"""
            var cell = state.LinkCache.ResolveContext<ICell, ICellGetter>(FormKey.Factory("{run.Plugins.InteriorCell}")).GetOrAddAsOverride(state.PatchMod);
            cell.Temporary.Add(new PlacedObject(state.PatchMod, "SafePatchNewRef") {"{"} Base = new FormLinkNullable<IPlaceableObjectGetter>(FormKey.Factory("{run.Plugins.Sword}")), Placement = new Placement() {"}"});
            """);

        using (var denied = new HostRun())
        {
            var e = Assert.Throws<PatchRejectedException>(() => denied.Run(add(denied)));
            Assert.Contains("creating PlacedObject records is not allowed", e.Message);
        }

        using var allowed = new HostRun();
        var report = allowed.Run(add(allowed), creatable: ["PlacedObject"]);

        var change = Assert.Single(report.Changes);
        Assert.True(change.IsNew);
        var cell = allowed.PatchMod.EnumerateMajorRecords<ICellGetter>().Single();
        Assert.Equal("SafePatchNewRef", Assert.Single(cell.Temporary).EditorID);
    }

    [Fact]
    public void A_cell_header_change_outside_the_manifest_is_rejected()
    {
        using var run = new HostRun();
        var program = Program($"""
            state.LinkCache.ResolveContext<ICell, ICellGetter>(FormKey.Factory("{run.Plugins.InteriorCell}")).GetOrAddAsOverride(state.PatchMod).WaterHeight = 5;
            """);

        var e = Assert.Throws<PatchRejectedException>(() => run.Run(program, writable: ["PlacedObject.*"]));

        Assert.Contains("changing Cell.WaterHeight is not allowed", e.Message);
        // The children the cell holds are not treated as changes to the cell.
        Assert.DoesNotContain("Cell.Temporary", e.Message);
    }

    [Fact]
    public void A_dialog_response_is_patched_inside_its_topic()
    {
        using var run = new HostRun();
        var program = Program($"""
            state.LinkCache.ResolveContext<IDialogResponses, IDialogResponsesGetter>(FormKey.Factory("{run.Plugins.TopicResponses}")).GetOrAddAsOverride(state.PatchMod).Prompt = "Greetings";
            """);

        var report = run.Run(program, writable: ["DialogResponses.Prompt"]);

        var change = Assert.Single(report.Changes);
        Assert.Equal(("DialogResponses", "Prompt"), (change.RecordType, Assert.Single(change.Fields)));
        var topic = Assert.Single(run.PatchMod.DialogTopics);
        Assert.Equal("Greetings", Assert.Single(topic.Responses).Prompt?.String);
    }

    [Fact]
    public void Rerunning_over_its_own_output_changes_nothing()
    {
        using var folder = new TempFolder("SafePatchPrevious-");
        var previous = folder.File("Synthesis.esp");
        using (var first = new HostRun())
        {
            first.Run(MoveRef(first.Plugins.InteriorRef), writable: ["PlacedObject.Placement"]);
            first.PatchMod.WriteToBinary(previous);
        }

        using var second = new HostRun(previousPatchSource: previous);
        var report = second.Run(MoveRef(second.Plugins.InteriorRef), writable: ["PlacedObject.Placement"]);

        Assert.Empty(report.Changes);
        Assert.Single(second.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>());
    }
}
