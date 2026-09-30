using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SafePatch.TestSupport;

/// <summary>
/// A load order with one record for each kind of conflict. Skyrim.esm defines the records; A.esp and B.esp override
/// them independently (each masters only Skyrim.esm); Patch.esp masters all three.
/// <list type="bullet">
/// <item><see cref="LostSword"/>: A raises its value, B renames it: A's BasicStats edit is lost (conflict).</item>
/// <item><see cref="PatchedSword"/>: the same, but Patch.esp wins with B's version: A's BasicStats edit is lost, resolved.</item>
/// <item><see cref="ItmSword"/>: A's override is identical to Skyrim.esm's (ITM).</item>
/// <item><see cref="CleanPotion"/>: A alone changes Value (a clean override).</item>
/// <item><see cref="DeletedSword"/>: B deletes it.</item>
/// <item><see cref="AddedList"/>: A adds AItem, B removes ItmSword: A's addition is lost, B's removal kept.</item>
/// <item><see cref="RemovedList"/>: A removes ItmSword, B adds BItem: A's removal is lost (ItmSword is back).</item>
/// </list>
/// </summary>
public sealed class ConflictFixture
{
    public const string A = "A.esp";
    public const string B = "B.esp";
    public const string Patch = "Patch.esp";

    public required FormKey LostSword { get; init; }
    public required FormKey PatchedSword { get; init; }
    public required FormKey ItmSword { get; init; }
    public required FormKey CleanPotion { get; init; }
    public required FormKey DeletedSword { get; init; }
    public required FormKey AddedList { get; init; }
    public required FormKey RemovedList { get; init; }
    public required FormKey AItem { get; init; }
    public required FormKey BItem { get; init; }

    /// <summary>Writes the plugins to <paramref name="data"/> and returns the fixture with a plugins.txt beside it.</summary>
    public static (ConflictFixture Fixture, string PluginsFile) Write(string data)
    {
        Directory.CreateDirectory(data);
        var skyrim = new SkyrimMod(ModKey.FromFileName("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        skyrim.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        var lost = Sword(skyrim, "LostSword");
        var patched = Sword(skyrim, "PatchedSword");
        var itm = Sword(skyrim, "ItmSword");
        var deleted = Sword(skyrim, "DeletedSword");
        var potion = skyrim.Ingestibles.AddNew("CleanPotion");
        potion.Value = 5;
        var added = List(skyrim, "AddedList", lost, itm);
        var removed = List(skyrim, "RemovedList", lost, itm);

        var a = new SkyrimMod(ModKey.FromFileName(A), SkyrimRelease.SkyrimSE);
        var aItem = Sword(a, "AItem");
        Override(a.Weapons, lost, w => w.BasicStats!.Value = 20);
        Override(a.Weapons, patched, w => w.BasicStats!.Value = 20);
        Override(a.Weapons, itm, _ => { });
        Override(a.Ingestibles, potion, p => p.Value = 7);
        Override(a.LeveledItems, added, l => l.Entries!.Add(Entry(aItem)));
        Override(a.LeveledItems, removed, l => l.Entries!.RemoveAt(1));

        var b = new SkyrimMod(ModKey.FromFileName(B), SkyrimRelease.SkyrimSE);
        var bItem = Sword(b, "BItem");
        Override(b.Weapons, lost, w => w.Name = "Blade");
        var bPatched = Override(b.Weapons, patched, w => w.Name = "Blade");
        Override(b.Weapons, deleted, w => w.IsDeleted = true);
        Override(b.LeveledItems, added, l => l.Entries!.RemoveAt(1));
        Override(b.LeveledItems, removed, l => l.Entries!.Add(Entry(bItem)));

        // The patch keeps B's sword, and its own list of A's and B's items makes it master both.
        var patch = new SkyrimMod(ModKey.FromFileName(Patch), SkyrimRelease.SkyrimSE);
        patch.Weapons.Set((Weapon)bPatched.DeepCopy());
        var patchList = patch.LeveledItems.AddNew("PatchList");
        patchList.Entries = [Entry(aItem), Entry(bItem)];

        skyrim.WriteToBinary(Path.Combine(data, "Skyrim.esm"));
        a.WriteToBinary(Path.Combine(data, A));
        b.WriteToBinary(Path.Combine(data, B));
        patch.WriteToBinary(Path.Combine(data, Patch));
        var plugins = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(data))!, "plugins.txt");
        File.WriteAllLines(plugins, [$"*{A}", $"*{B}", $"*{Patch}"]);

        return (new ConflictFixture
        {
            LostSword = lost.FormKey, PatchedSword = patched.FormKey, ItmSword = itm.FormKey, CleanPotion = potion.FormKey,
            DeletedSword = deleted.FormKey, AddedList = added.FormKey, RemovedList = removed.FormKey, AItem = aItem.FormKey, BItem = bItem.FormKey,
        }, plugins);
    }

    private static Weapon Sword(SkyrimMod mod, string editorId)
    {
        var sword = mod.Weapons.AddNew(editorId);
        sword.Name = editorId;
        sword.BasicStats = new WeaponBasicStats { Value = 10, Weight = 9, Damage = 7 };
        return sword;
    }

    private static LeveledItem List(SkyrimMod mod, string editorId, params IItemGetter[] items)
    {
        var list = mod.LeveledItems.AddNew(editorId);
        list.Entries = [.. items.Select(Entry)];
        return list;
    }

    private static T Override<T>(SkyrimGroup<T> group, T record, Action<T> change) where T : SkyrimMajorRecord, IMajorRecordInternal
    {
        var copy = (T)record.DeepCopy();
        change(copy);
        group.Set(copy);
        return copy;
    }

    private static LeveledItemEntry Entry(IItemGetter item) => new()
    {
        Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(item.FormKey) },
    };
}
