using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SafePatch.TestSupport;

/// <summary>
/// A synthetic load order at the scale of a large modded setup: a base plugin with many leveled lists,
/// a few near-full lists and a cell with many references, then many plugins that each override some
/// lists and add their own item, so every list has a dense override chain that a merge has to repair.
/// </summary>
public sealed record ScaleFixture(int Plugins, int Lists, int OverridesPerList, int EntriesPerList, int References, int LargeLists)
{
    public const string BaseName = "ScaleBase.esp";
    public const string CellEditorId = "ScaleCell";

    /// <summary>
    /// A modded setup of hundreds of plugins. <c>SAFEPATCH_SCALE_PLUGINS</c> sets the plugin count, and
    /// <c>SAFEPATCH_SCALE_RECORDS</c> multiplies the lists and references.
    /// </summary>
    public static ScaleFixture Default { get; } = Create(Setting("SAFEPATCH_SCALE_PLUGINS", 500), Setting("SAFEPATCH_SCALE_RECORDS", 1));

    private static ScaleFixture Create(int plugins, int records) =>
        new(plugins, Lists: 2000 * records, OverridesPerList: 6, EntriesPerList: 8, References: 5000 * records, LargeLists: 5);

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n > 0 ? n : fallback;

    /// <summary>Near the game's 255-entry limit, so a merge can still add every override's entry.</summary>
    public const int LargeListEntries = 200;

    /// <summary>The base plugin, then the overriding plugins, in load order.</summary>
    public IReadOnlyList<SkyrimMod> Build()
    {
        var baseMod = new SkyrimMod(ModKey.FromFileName(BaseName), SkyrimRelease.SkyrimSE);
        var items = Enumerable.Range(0, Math.Max(EntriesPerList, LargeListEntries)).Select(i => baseMod.Weapons.AddNew($"ScaleItem{i}")).ToList();
        var lists = Enumerable.Range(0, Lists + LargeLists).Select(i =>
        {
            var list = baseMod.LeveledItems.AddNew($"ScaleList{i}");
            list.Entries = [.. items.Take(i < Lists ? EntriesPerList : LargeListEntries).Select(item => Entry(item.FormKey, level: 1))];
            return list;
        }).ToList();
        AddCell(baseMod, items[0]);

        var mods = new List<SkyrimMod> { baseMod };
        var overriders = Enumerable.Range(0, Plugins).Select(p => new List<int>()).ToList();
        for (var list = 0; list < lists.Count; list++)
        {
            // Large lists are overridden by more plugins, which stresses one record's chain.
            var count = list < Lists ? OverridesPerList : Math.Min(Plugins, LargeListEntries / 10);
            for (var k = 0; k < count; k++) overriders[(int)(((long)list * 7919 + k * 104729) % Plugins)].Add(list);
        }
        for (var p = 0; p < Plugins; p++)
        {
            var mod = new SkyrimMod(ModKey.FromFileName($"Scale{p:D4}.esp"), SkyrimRelease.SkyrimSE);
            var own = mod.Weapons.AddNew($"ScaleOwn{p}");
            foreach (var index in overriders[p].Distinct())
            {
                // Each override carries the base entries plus its own item, so later overrides drop earlier additions.
                var copy = (LeveledItem)lists[index].DeepCopy();
                copy.Entries!.Add(Entry(own.FormKey, level: (short)(1 + (p % 50))));
                mod.LeveledItems.Set(copy);
            }
            mods.Add(mod);
        }
        return mods;
    }

    private void AddCell(SkyrimMod mod, Weapon placeable)
    {
        var cell = new Cell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = CellEditorId, Flags = Cell.Flag.IsInteriorCell };
        for (var i = 0; i < References; i++)
        {
            cell.Temporary.Add(new PlacedObject(mod, $"ScaleRef{i}")
            {
                Base = placeable.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(i % 100, i / 100, 0) },
            });
        }
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [cell] });
        mod.Cells.Records.Add(block);
    }

    private static LeveledItemEntry Entry(FormKey item, short level) =>
        new() { Data = new LeveledItemEntryData { Level = level, Count = 1, Reference = new FormLink<IItemGetter>(item) } };

    /// <summary>
    /// A general leveled-list merge (every entry any override added, restored to the winner) plus a pass
    /// that rescales every reference in the scale cell.
    /// </summary>
    public const string MergeProgram = """
        using System.Collections.Generic;
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class ScaleMerge
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                var merged = 0;
                foreach (var winner in state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides())
                {
                    var chain = state.LinkCache.ResolveAllContexts<ILeveledItem, ILeveledItemGetter>(winner.Record.FormKey).ToList();
                    if (chain.Count < 3) continue;
                    var present = Entries(winner.Record).ToHashSet();
                    var missing = chain.Skip(1).SelectMany(c => Entries(c.Record)).Where(e => present.Add(e)).ToList();
                    if (missing.Count == 0) continue;
                    var patched = winner.GetOrAddAsOverride(state.PatchMod);
                    foreach (var (level, reference, count) in missing)
                        patched.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = level, Count = count, Reference = new FormLink<IItemGetter>(reference) } });
                    merged++;
                }

                var rescaled = 0;
                foreach (var placed in state.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(state.LinkCache))
                {
                    if (placed.Record.EditorID?.StartsWith("ScaleRef") != true) continue;
                    placed.GetOrAddAsOverride(state.PatchMod).Scale = 1.5f;
                    rescaled++;
                }
                System.Console.WriteLine($"Merged {merged} lists, rescaled {rescaled} references.");
            }

            private static IEnumerable<(short Level, FormKey Reference, short Count)> Entries(ILeveledItemGetter list) =>
                (list.Entries ?? []).Where(e => e.Data is not null).Select(e => (e.Data!.Level, e.Data.Reference.FormKey, e.Data.Count));
        }
        """;

    public static readonly string[] MergeWritable = ["LeveledItem.Entries", "PlacedObject.Scale"];
}
