namespace SafePatch.TestSupport;

/// <summary>Agent-style programs, written exactly as a native Synthesis patch function.</summary>
public static class SamplePrograms
{
    /// <summary>
    /// Restores entries that CACO added to a leveled list when a later mod's override dropped them.
    /// </summary>
    public const string LeveledListMerge = """
        using System.Collections.Generic;
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class MergeLeveledLists
        {
            private static readonly ModKey Source = ModKey.FromFileName("Complete Alchemy & Cooking Overhaul.esp");

            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                foreach (var winner in state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides())
                {
                    if (winner.ModKey == Source) continue;
                    // Highest priority first; the last is the original definition.
                    var chain = state.LinkCache.ResolveAllContexts<ILeveledItem, ILeveledItemGetter>(winner.Record.FormKey).ToList();
                    var source = chain.FirstOrDefault(c => c.ModKey == Source);
                    if (source is null) continue;

                    var added = Except(Entries(source.Record), Entries(chain[^1].Record));
                    var missing = Except(added, Entries(winner.Record));
                    if (missing.Count == 0) continue;

                    var patched = winner.GetOrAddAsOverride(state.PatchMod);
                    foreach (var (level, reference, count) in missing)
                    {
                        patched.Entries!.Add(new LeveledItemEntry
                        {
                            Data = new LeveledItemEntryData { Level = level, Count = count, Reference = new FormLink<IItemGetter>(reference) },
                        });
                    }
                    System.Console.WriteLine($"Restored {missing.Count} entries to {winner.Record.EditorID}.");
                }
            }

            private static List<(short Level, FormKey Reference, short Count)> Entries(ILeveledItemGetter list) =>
                (list.Entries ?? []).Where(e => e.Data is not null).Select(e => (e.Data!.Level, e.Data.Reference.FormKey, e.Data.Count)).ToList();

            // Multiset difference: each entry in b cancels one equal entry in a.
            private static List<(short Level, FormKey Reference, short Count)> Except(
                IEnumerable<(short Level, FormKey Reference, short Count)> a, IEnumerable<(short Level, FormKey Reference, short Count)> b)
            {
                var remaining = b.GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
                var result = new List<(short Level, FormKey Reference, short Count)>();
                foreach (var e in a)
                {
                    if (remaining.TryGetValue(e, out var n) && n > 0) remaining[e] = n - 1;
                    else result.Add(e);
                }
                return result;
            }
        }
        """;

    /// <summary>The manifest scope the merge needs.</summary>
    public static readonly string[] LeveledListMergeWritable = ["LeveledItem.Entries"];

    /// <summary>Data-only settings, as Synthesis's settings GUI shows them.</summary>
    public const string NamingSettings = """
        using System.Collections.Generic;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis.Settings;

        namespace Naming;

        public enum Mode { Create, Skip }

        public class NamingSettings
        {
            [SynthesisSettingName("Mode")]
            [SynthesisTooltip("Skip leaves the patch alone.")]
            public Mode Mode { get; set; } = Mode.Create;

            public string EditorId { get; set; } = "LItemDefault";

            public List<FormLink<IItemGetter>> Items { get; set; } = new();

            public int Level = 1;
        }
        """;

    /// <summary>Creates a leveled list named by its settings, receiving them the native way.</summary>
    public const string NamedList = """
        using System;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class NamedList
        {
            public static Lazy<Naming.NamingSettings> Settings = null!;

            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                if (Settings.Value.Mode == Naming.Mode.Skip) return;
                state.PatchMod.LeveledItems.AddNew(Settings.Value.EditorId);
            }
        }
        """;
}
