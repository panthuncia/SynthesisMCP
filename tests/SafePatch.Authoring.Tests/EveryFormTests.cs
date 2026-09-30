using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>
/// Every record type a real game has, through the whole patch path: the sandboxed program, Synthesis's pipeline, the
/// host's field-level validation and the commit. Needs a real Skyrim Data folder, so it runs only when
/// <c>SAFEPATCH_GAME_DATA</c> (the Data folder) and <c>SAFEPATCH_GAME_PLUGINS</c> (a plugins.txt) are set; it only reads them.
/// </summary>
[Trait("Category", "Game")]
public sealed class EveryFormTests
{
    /// <summary>Overrides a few winning records of every type, renaming each.</summary>
    private const string OverrideEveryType = """
        using System.Collections.Generic;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins.Records;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class EveryType
        {
            // Overrides the first few winning records of every record type, renaming each, to exercise every form's path.
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                var perType = new Dictionary<string, int>();
                foreach (var context in state.LoadOrder.PriorityOrder.WinningContextOverrides<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>(state.LinkCache))
                {
                    var type = context.Record.Registration.Name;
                    var seen = perType.GetValueOrDefault(type);
                    if (seen >= 3) continue;
                    perType[type] = seen + 1;
                    var record = context.GetOrAddAsOverride(state.PatchMod);
                    record.EditorID = (record.EditorID ?? "Unnamed") + "_SP";
                }
                foreach (var (type, count) in perType) System.Console.WriteLine($"{type} {count}");
            }
        }
        """;

    /// <summary>Duplicates a few records of every type as new records, and reverts a few overridden ones to their original version.</summary>
    private const string CreateAndRevertEveryType = """
        using System.Collections.Generic;
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Plugins.Cache;
        using Mutagen.Bethesda.Plugins.Records;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class EveryTypeCreateAndRevert
        {
            // For every record type: duplicates a few winners as new records, and reverts a few overridden records to their
            // original version, which changes whole field sets (lists, nested data, links) at once.
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                var created = new Dictionary<string, int>();
                var toRevert = new Dictionary<string, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>>();
                foreach (var context in state.LoadOrder.PriorityOrder.WinningContextOverrides<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>(state.LinkCache))
                {
                    var type = context.Record.Registration.Name;
                    if (created.GetValueOrDefault(type) < 2 && context.Record is not (ICellGetter or IWorldspaceGetter or IDialogTopicGetter))
                    {
                        created[type] = created.GetValueOrDefault(type) + 1;
                        var copy = context.DuplicateIntoAsNewRecord(state.PatchMod);
                        copy.EditorID = (context.Record.EditorID ?? "Unnamed") + "_New";
                    }
                    // Overridden: the winning version is not from the record's own plugin.
                    if (context.ModKey != context.Record.FormKey.ModKey && context.ModKey != state.PatchMod.ModKey)
                    {
                        if (!toRevert.TryGetValue(type, out var list)) toRevert[type] = list = new();
                        if (list.Count < 2) list.Add(context);
                    }
                }

                // Each original is read from its own plugin, one pass per plugin.
                var reverted = 0;
                foreach (var byOrigin in toRevert.Values.SelectMany(l => l).GroupBy(c => c.Record.FormKey.ModKey))
                {
                    var listing = state.LoadOrder.TryGetValue(byOrigin.Key);
                    if (listing?.Mod is null) continue;
                    var wanted = byOrigin.ToDictionary(c => c.Record.FormKey);
                    foreach (var original in listing.Mod.EnumerateMajorRecords())
                    {
                        if (!wanted.TryGetValue(original.FormKey, out var context) || original.Equals(context.Record)) continue;
                        // Containers would copy their child records too, which are records of their own.
                        if (original is ICellGetter or IWorldspaceGetter or IDialogTopicGetter) continue;
                        // Reverting to links the load order no longer has would break them; the host rightly refuses that.
                        var winnerLinks = context.Record.EnumerateFormLinks().Select(l => l.FormKey).ToHashSet();
                        if (original.EnumerateFormLinks().Any(l => !l.IsNull && !winnerLinks.Contains(l.FormKey))) continue;
                        var record = context.GetOrAddAsOverride(state.PatchMod);
                        ((IMajorRecordInternal)record).DeepCopyIn(original);
                        reverted++;
                    }
                }
                System.Console.WriteLine($"created {created.Values.Sum()} in {created.Count} types; reverted {reverted} in {toRevert.Count} types");
            }
        }
        """;

    private static AuthoringService Game(out LoadOrderSnapshot snapshot)
    {
        var data = Environment.GetEnvironmentVariable("SAFEPATCH_GAME_DATA");
        var plugins = Environment.GetEnvironmentVariable("SAFEPATCH_GAME_PLUGINS");
        if (data is null || plugins is null) Assert.Skip("Set SAFEPATCH_GAME_DATA and SAFEPATCH_GAME_PLUGINS to a Skyrim Data folder and plugins.txt.");
        snapshot = LoadOrderSnapshot.Open(data, plugins);
        return new AuthoringService(snapshot);
    }

    private static IReadOnlySet<string> TypesPresent(LoadOrderSnapshot snapshot) =>
        new Query.QueryService(snapshot).DescribeType().Rows.Where(r => r[1] != "0").Select(r => r[0]!).ToHashSet();

    [Fact]
    public void Every_record_type_in_the_game_can_be_overridden()
    {
        var service = Game(out var snapshot);
        using (snapshot)
        {
            var result = service.TestPatch(OverrideEveryType, new PatchScope(["*"], MaxRecords: 100_000), gameIni: "", cancel: TestContext.Current.CancellationToken);

            Assert.True(result.Accepted, result.Error);
            Assert.Equal(TypesPresent(snapshot), result.Changes.Select(c => c.Change.RecordType).ToHashSet());
        }
    }

    [Fact]
    public void Every_record_type_but_containers_can_be_created_and_whole_records_reverted()
    {
        var service = Game(out var snapshot);
        using (snapshot)
        {
            var result = service.TestPatch(CreateAndRevertEveryType, new PatchScope(["*"], Creatable: ["*"], MaxRecords: 100_000), gameIni: "", cancel: TestContext.Current.CancellationToken);

            Assert.True(result.Accepted, result.Error);
            var created = result.Changes.Where(c => c.Change.IsNew).Select(c => c.Change.RecordType).ToHashSet();
            Assert.Equal(TypesPresent(snapshot).Except(["Cell", "Worldspace", "DialogTopic"]).ToHashSet(), created);
            Assert.Contains(result.Changes, c => !c.Change.IsNew && c.Change.Fields.Count > 1);
        }
    }
}
