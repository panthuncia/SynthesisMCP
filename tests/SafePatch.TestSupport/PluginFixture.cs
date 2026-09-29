using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SafePatch.TestSupport;

/// <summary>
/// Real plugin files for the standard conflict: Skyrim.esm defines a list, CACO adds a potion to it,
/// and a later mod's override adds an armor but drops CACO's potion.
/// </summary>
public sealed class PluginFixture
{
    public const string BaseMaster = "Skyrim.esm";
    public const string Caco = "Complete Alchemy & Cooking Overhaul.esp";
    public const string Other = "OtherMod.esp";
    public const string ListEditorId = "LItemSafePatchTest";

    public FormKey List { get; private init; }
    public FormKey Sword { get; private init; }
    public FormKey Potion { get; private init; }
    public FormKey CacoPotion { get; private init; }
    public FormKey OtherArmor { get; private init; }
    public FormKey Npc { get; private init; }
    public FormKey Owner { get; private init; }

    /// <summary>An interior cell holding <see cref="InteriorRef"/>.</summary>
    public FormKey InteriorCell { get; private init; }
    public FormKey InteriorRef { get; private init; }
    public FormKey Worldspace { get; private init; }
    /// <summary>An exterior cell at (0, 0) in <see cref="Worldspace"/>, holding <see cref="ExteriorRef"/>.</summary>
    public FormKey ExteriorCell { get; private init; }
    public FormKey ExteriorRef { get; private init; }
    /// <summary>A reference in the worldspace's persistent cell.</summary>
    public FormKey PersistentRef { get; private init; }
    public FormKey Topic { get; private init; }
    public FormKey TopicResponses { get; private init; }

    /// <summary>Writes Skyrim.esm to <paramref name="masterFolder"/> and the two conflicting plugins to <paramref name="pluginFolder"/>.</summary>
    public static PluginFixture Write(string masterFolder, string? pluginFolder = null)
    {
        pluginFolder ??= masterFolder;
        var baseMod = new SkyrimMod(ModKey.FromFileName(BaseMaster), SkyrimRelease.SkyrimSE);
        baseMod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        var sword = baseMod.Weapons.AddNew("IronSword");
        var potion = baseMod.Ingestibles.AddNew("RestoreHealth01");
        var npc = baseMod.Npcs.AddNew("SomeNpc");
        var owner = baseMod.Factions.AddNew("Owners");
        var list = baseMod.LeveledItems.AddNew(ListEditorId);
        list.Entries = [Entry(sword, owner: owner), Entry(potion)];
        var nested = AddNestedRecords(baseMod, sword);

        var caco = new SkyrimMod(ModKey.FromFileName(Caco), SkyrimRelease.SkyrimSE);
        var cacoPotion = caco.Ingestibles.AddNew("CACO_Potion");
        var cacoList = (LeveledItem)list.DeepCopy();
        cacoList.Entries!.Add(Entry(cacoPotion));
        caco.LeveledItems.Set(cacoList);

        var other = new SkyrimMod(ModKey.FromFileName(Other), SkyrimRelease.SkyrimSE);
        var armor = other.Armors.AddNew("OtherArmor");
        var otherList = (LeveledItem)list.DeepCopy();
        otherList.Entries!.Add(Entry(armor, level: 5));
        other.LeveledItems.Set(otherList);

        Directory.CreateDirectory(masterFolder);
        Directory.CreateDirectory(pluginFolder);
        baseMod.WriteToBinary(Path.Combine(masterFolder, BaseMaster));
        caco.WriteToBinary(Path.Combine(pluginFolder, Caco));
        other.WriteToBinary(Path.Combine(pluginFolder, Other));

        return new PluginFixture
        {
            List = list.FormKey, Sword = sword.FormKey, Potion = potion.FormKey, CacoPotion = cacoPotion.FormKey,
            OtherArmor = armor.FormKey, Npc = npc.FormKey, Owner = owner.FormKey,
            InteriorCell = nested.InteriorCell, InteriorRef = nested.InteriorRef, Worldspace = nested.Worldspace,
            ExteriorCell = nested.ExteriorCell, ExteriorRef = nested.ExteriorRef, PersistentRef = nested.PersistentRef,
            Topic = nested.Topic, TopicResponses = nested.TopicResponses,
        };
    }

    /// <summary>A Skyrim SE plugins.txt enabling both conflicting plugins, CACO first.</summary>
    public static string WriteLoadOrder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, ["# SafePatch test load order", $"*{Caco}", $"*{Other}"]);
        return path;
    }

    /// <summary>The <c>run-patcher</c> arguments Synthesis passes a patcher, as in a real run.</summary>
    public static IReadOnlyList<string> RunPatcherArguments(
        string dataFolder, string loadOrderFile, string outputPath, string? sourcePath = null, string? extraDataFolder = null,
        string? persistenceFolder = null, string patcherName = "SafePatchTest", IReadOnlyList<string>? extra = null) =>
    [
        "run-patcher",
        "--GameRelease", "SkyrimSE",
        "--DataFolderPath", dataFolder,
        "--LoadOrderFilePath", loadOrderFile,
        "--OutputPath", outputPath,
        "--ModKey", Path.GetFileName(outputPath),
        .. sourcePath is null ? Array.Empty<string>() : ["--SourcePath", sourcePath],
        .. extraDataFolder is null ? Array.Empty<string>() : ["--ExtraDataFolder", extraDataFolder],
        .. persistenceFolder is null ? Array.Empty<string>() : ["--PersistencePath", persistenceFolder, "--PatcherName", patcherName],
        .. extra ?? [],
    ];

    private sealed record Nested(FormKey InteriorCell, FormKey InteriorRef, FormKey Worldspace, FormKey ExteriorCell,
        FormKey ExteriorRef, FormKey PersistentRef, FormKey Topic, FormKey TopicResponses);

    /// <summary>Records that live inside other records: cells and their references, and a dialog topic's responses.</summary>
    private static Nested AddNestedRecords(SkyrimMod mod, Weapon placeable)
    {
        PlacedObject Ref(string editorId) => new(mod, editorId)
        {
            Base = placeable.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement(),
        };

        var interiorRef = Ref("SafePatchInteriorRef");
        var interior = new Cell(mod, "SafePatchInterior") { Flags = Cell.Flag.IsInteriorCell };
        interior.Temporary.Add(interiorRef);
        var id = interior.FormKey.ID;
        mod.Cells.Records.Add(new CellBlock
        {
            BlockNumber = (int)(id % 10),
            GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks =
            [
                new CellSubBlock { BlockNumber = (int)(id / 10 % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [interior] },
            ],
        });

        var exteriorRef = Ref("SafePatchExteriorRef");
        var exterior = new Cell(mod, "SafePatchExterior") { Grid = new CellGrid { Point = new P2Int(0, 0) } };
        exterior.Temporary.Add(exteriorRef);
        var persistentRef = Ref("SafePatchPersistentRef");
        var persistent = new Cell(mod) { Flags = Cell.Flag.HasWater };
        persistent.Persistent.Add(persistentRef);
        var worldspace = mod.Worldspaces.AddNew("SafePatchWorld");
        worldspace.TopCell = persistent;
        worldspace.SubCells.Add(new WorldspaceBlock
        {
            BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
            Items =
            [
                new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = [exterior] },
            ],
        });

        var topic = mod.DialogTopics.AddNew("SafePatchTopic");
        var responses = new DialogResponses(mod) { Prompt = "Hello" };
        topic.Responses.Add(responses);

        return new Nested(interior.FormKey, interiorRef.FormKey, worldspace.FormKey, exterior.FormKey, exteriorRef.FormKey,
            persistentRef.FormKey, topic.FormKey, responses.FormKey);
    }

    private static LeveledItemEntry Entry(IItemGetter item, short level = 1, Faction? owner = null) => new()
    {
        Data = new LeveledItemEntryData { Level = level, Count = 1, Reference = new FormLink<IItemGetter>(item.FormKey) },
        ExtraData = owner is null ? null : new ExtraData { Owner = new FactionOwner { Faction = owner.ToLink() } },
    };
}
