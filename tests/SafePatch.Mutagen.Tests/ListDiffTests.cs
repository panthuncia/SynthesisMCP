using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace SafePatch.Mutagen.Tests;

/// <summary>Element-level list differences and generated field names, the building blocks of conflict analysis.</summary>
public class ListDiffTests
{
    private static readonly FormKey Sword = FormKey.Factory("000800:Skyrim.esm");
    private static readonly FormKey Potion = FormKey.Factory("000801:Skyrim.esm");

    private static LeveledItemEntry Entry(FormKey item, short level = 1) =>
        new() { Data = new LeveledItemEntryData { Level = level, Count = 1, Reference = new FormLink<IItemGetter>(item) } };

    [Fact]
    public void Lists_compare_as_multisets_by_value()
    {
        LeveledItemEntry[] before = [Entry(Sword), Entry(Potion), Entry(Potion)];
        LeveledItemEntry[] after = [Entry(Potion), Entry(Sword, level: 5), Entry(Sword)];

        var diff = ListDiff.Compare(before, after);

        Assert.Equal([Entry(Sword, level: 5)], diff.Added);
        Assert.Equal([Entry(Potion)], diff.Removed);
        Assert.True(ListDiff.Compare(before, before.Reverse().ToArray()).IsEmpty);
        Assert.Equal([Entry(Sword)], ListDiff.Compare(null, new[] { Entry(Sword) }).Added);
    }

    [Fact]
    public void Equal_elements_match_whatever_their_generated_hash_codes()
    {
        // Mutagen 0.54.4 hashes byte arrays and nested lists by reference, and compares byte arrays by reference, so
        // equal elements built or read apart (a rank placement's padding, an effect's conditions) do not match.
        RankPlacement Rank(FormKey faction) => new() { Faction = new FormLink<IFactionGetter>(faction), Rank = 1 };
        Effect Effect() => new()
        {
            BaseEffect = new FormLinkNullable<IMagicEffectGetter>(Sword),
            Conditions = [new ConditionFloat { ComparisonValue = 1, Data = new GetIsIDConditionData() }],
        };

        Assert.True(ListDiff.Compare(new[] { Rank(Sword), Rank(Potion) }, new[] { Rank(Potion), Rank(Sword) }).IsEmpty);
        Assert.True(ListDiff.Compare(new[] { Effect() }, new[] { Effect() }).IsEmpty);
        Assert.Equal([Rank(Potion)], ListDiff.Absent([Rank(Sword), Rank(Potion)], new[] { Rank(Sword) }));
        Assert.True(ListDiff.Compare(new[] { new MemorySlice<byte>([1, 2]) }, new[] { new MemorySlice<byte>([1, 2]) }).IsEmpty);
    }

    [Fact]
    public void Present_and_absent_count_duplicates()
    {
        object[] items = [Entry(Potion), Entry(Potion), Entry(Sword)];
        LeveledItemEntry[] list = [Entry(Potion)];

        Assert.Equal([Entry(Potion)], ListDiff.Present(items, list));
        Assert.Equal([Entry(Potion), Entry(Sword)], ListDiff.Absent(items, list));
    }

    [Fact]
    public void Text_bytes_and_translated_strings_are_not_lists()
    {
        Assert.True(ListDiff.IsList(new List<LeveledItemEntry>()));
        Assert.False(ListDiff.IsList("text"));
        Assert.False(ListDiff.IsList(new byte[] { 1 }));
        Assert.False(ListDiff.IsList(new MemorySlice<byte>([1])));
        Assert.False(ListDiff.IsList(new TranslatedString(Language.English, "Name")));
    }

    [Fact]
    public void Gendered_fields_differ_only_when_a_gender_does()
    {
        // A gendered field's mask is a pair of flags, male and female, rather than one.
        var mod = new SkyrimMod(ModKey.FromFileName("Test.esp"), SkyrimRelease.SkyrimSE);
        var addon = mod.ArmorAddons.AddNew("Hat");
        addon.Priority = new GenderedItem<byte>(10, 20);
        addon.WeightSliderEnabled = new GenderedItem<bool>(true, false);
        var copy = addon.DeepCopy();

        Assert.Empty(RecordDiff.ChangedFields(addon, copy));
        copy.Priority = new GenderedItem<byte>(10, 21);
        Assert.Equal(["Priority"], RecordDiff.ChangedFields(addon, copy));
    }

    [Fact]
    public void Field_names_come_from_the_generated_masks_base_fields_first_without_bookkeeping()
    {
        var list = RecordDiff.FieldNames(typeof(LeveledItem));
        var entry = RecordDiff.FieldNames(typeof(LeveledItemEntry));

        Assert.True(list.ToList().IndexOf("EditorID") < list.ToList().IndexOf("Entries"));
        Assert.DoesNotContain("FormKey", list);
        Assert.DoesNotContain("VersionControl", list);
        Assert.Equal(["Data", "ExtraData"], entry);
    }
}
