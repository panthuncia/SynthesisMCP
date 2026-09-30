using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Output;
using SafePatch.Authoring.Query;
using SafePatch.Host;
using SafePatch.Mutagen;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>The load-order queries over <see cref="ConflictFixture"/>: one record for each kind of conflict.</summary>
public sealed class QueryServiceTests : IDisposable
{
    private readonly TempFolder _temp = new("SafePatchQuery-");
    private readonly ConflictFixture _fixture;
    private readonly string _pluginsFile;
    private readonly LoadOrderSnapshot _snapshot;
    private readonly QueryService _queries;

    public QueryServiceTests()
    {
        (_fixture, _pluginsFile) = ConflictFixture.Write(Data);
        _snapshot = LoadOrderSnapshot.Open(Data, _pluginsFile);
        _queries = new QueryService(_snapshot);
    }

    private string Data => _temp.File("Data");

    private static string Text(ResultSet set) => Budgeted.Render(set, budget: int.MaxValue);

    private static List<string?> Column(ResultSet set, string column) => [.. set.Rows.Select(r => r[set.Columns.ToList().IndexOf(column)])];

    private List<string?> EditorIds(RecordFilter filter) => [.. Column(_queries.FindRecords(filter), "EditorID").Order()];

    [Fact]
    public void The_load_order_lists_each_plugin_with_flags_masters_and_missing_masters()
    {
        var set = _queries.LoadOrder();

        Assert.Equal(["Skyrim.esm", ConflictFixture.A, ConflictFixture.B, ConflictFixture.Patch], Column(set, "Plugin"));
        Assert.Equal(["MASTER", "PLUGIN", "PLUGIN", "PLUGIN"], Column(set, "Flags"));
        Assert.Equal("3", Column(set, "Masters")[3]);
        Assert.All(Column(set, "Missing masters"), Assert.Null);

        File.WriteAllLines(_pluginsFile, [$"*{ConflictFixture.B}", $"*{ConflictFixture.Patch}"]);
        using var withoutA = LoadOrderSnapshot.Open(Data, _pluginsFile);
        Assert.Equal(ConflictFixture.A, Column(new QueryService(withoutA).LoadOrder(), "Missing masters")[2]);
    }

    [Fact]
    public void A_plugin_shows_its_header_and_its_new_records_and_overrides_by_type()
    {
        var set = _queries.LoadOrder(ConflictFixture.A);

        Assert.Contains("Masters: Skyrim.esm", set.Notes!);
        Assert.Contains("Records: 1 new, 6 overrides.", set.Notes!);
        Assert.Equal(["Weapon", "1", "3"], set.Rows.Single(r => r[0] == "Weapon"));
        Assert.Throws<SafePatchException>(() => _queries.LoadOrder("Nope.esp"));
    }

    [Fact]
    public void Records_are_found_by_type_EditorID_name_and_plugin_role()
    {
        // A deleted winner has no EditorID of its own; it keeps the one its earlier version had.
        Assert.Equal(["DeletedSword", "ItmSword", "LostSword", "PatchedSword"], EditorIds(new RecordFilter(["Weapon"], EditorId: "*sword")));
        Assert.Equal(["LostSword", "PatchedSword"], EditorIds(new RecordFilter(["Weapon"], Name: "blade")));
        Assert.Equal(["AItem"], EditorIds(new RecordFilter(Plugin: ConflictFixture.A, Role: PluginRole.Defines)));
        Assert.Equal(["AItem", "CleanPotion", "ItmSword"], EditorIds(new RecordFilter(Plugin: ConflictFixture.A, Role: PluginRole.Wins)));
        Assert.Equal(["AddedList", "CleanPotion", "ItmSword", "LostSword", "PatchedSword", "RemovedList"],
            EditorIds(new RecordFilter(Plugin: ConflictFixture.A, Role: PluginRole.Overrides)));
        Assert.Equal(["AItem", "BItem"], EditorIds(new RecordFilter(["Item"], EditorId: "?Item")));
        // A star that must give back what it first took ("*s?o*D" against "DeletedSword": the s of "Deleted" fails).
        Assert.Equal(["DeletedSword", "ItmSword", "LostSword", "PatchedSword"], EditorIds(new RecordFilter(["Weapon"], EditorId: "*s?o*D")));
        Assert.Equal(["LostSword"], EditorIds(new RecordFilter(["Weapon"], EditorId: "L*S*")));
        Assert.Empty(EditorIds(new RecordFilter(["Weapon"], EditorId: "*sword?")));
        Assert.Empty(EditorIds(new RecordFilter(["Weapon"], EditorId: "sword")));
    }

    [Fact]
    public void Records_are_found_by_field_conditions_links_and_version_counts()
    {
        Assert.Equal(["CleanPotion"], EditorIds(new RecordFilter(["Ingestible"], Where: "Value == 7")));
        Assert.Equal(["LostSword", "PatchedSword"], EditorIds(new RecordFilter(["Weapon"], Where: "BasicStats.Value <= 10 and Name contains Blade")));
        Assert.Equal(["PatchList", "RemovedList"], EditorIds(new RecordFilter(["LeveledItem"], Where: "Entries contains BItem")));
        Assert.Equal(["AddedList"], EditorIds(new RecordFilter(["LeveledItem"], Where: "Entries.Count == 1")));
        Assert.Equal(["PatchList"], EditorIds(new RecordFilter(["LeveledItem"], LinksTo: "AItem")));
        Assert.Equal(["AddedList", "LostSword", "PatchedSword", "RemovedList"], EditorIds(new RecordFilter(["Weapon", "LeveledItem"], MinVersions: 3)));
        Assert.Equal("4", _queries.FindRecords(new RecordFilter(["Weapon"], EditorId: "PatchedSword")).Rows.Single()[5]);

        var error = Assert.Throws<SafePatchException>(() => _queries.FindRecords(new RecordFilter(["Weapon"], Where: "Valu > 1")));
        Assert.Contains("BasicStats", error.Message);
        Assert.Throws<SafePatchException>(() => _queries.FindRecords(new RecordFilter(["Weapon"], Where: "Value is big")));
    }

    [Fact]
    public void A_record_shows_its_set_fields_with_links_named_and_any_version_on_request()
    {
        var winner = Text(_queries.GetRecord("LostSword"));
        var fromA = Text(_queries.GetRecord(_fixture.LostSword.ToString(), ConflictFixture.A));
        var entries = _queries.GetRecord("RemovedList", fields: ["Entries"]);

        Assert.Contains("Versions: Skyrim.esm → A.esp → B.esp", winner);
        Assert.Contains("Name: Blade", winner);
        Assert.Contains("BasicStats: {Value: 10, Weight: 9, Damage: 7}", winner);
        Assert.DoesNotContain("MajorRecordFlagsRaw", winner);
        Assert.Contains("(version from A.esp)", fromA);
        Assert.Contains("BasicStats: {Value: 20", fromA);
        Assert.Contains(entries.Rows, r => r[0]!.Contains($"{_fixture.BItem} BItem"));
        Assert.Throws<SafePatchException>(() => _queries.GetRecord("LostSword", fields: ["NoSuchField"]));
        Assert.Throws<SafePatchException>(() => _queries.GetRecord("NoSuchRecord"));
        Assert.Throws<SafePatchException>(() => _queries.GetRecord("AItem", ConflictFixture.B));
    }

    [Fact]
    public void Record_types_and_their_fields_are_described_with_the_names_programs_use()
    {
        Assert.Contains(["LeveledItem", "3"], _queries.DescribeType().Rows);
        Assert.Contains(["Weapon", "6"], _queries.DescribeType().Rows);

        var fields = _queries.DescribeType("LeveledItem");
        Assert.Equal(RecordDiff.FieldNames(typeof(LeveledItem)), Column(fields, "Field").Where(f => !f!.Contains('.') && !f.Contains('[')));
        Assert.Contains(["Entries", "list of LeveledItemEntry", ""], fields.Rows);
        Assert.Contains(["Entries[].Data.Reference", "link → Item", ""], fields.Rows);
        Assert.Throws<SafePatchException>(() => _queries.DescribeType("Item"));
        Assert.Throws<SafePatchException>(() => _queries.DescribeType("Nonsense"));
    }

    [Fact]
    public void Comparing_versions_shows_lost_list_entries_resolutions_ITMs_and_deletions()
    {
        var added = Text(_queries.CompareRecord("AddedList"));
        var removed = Text(_queries.CompareRecord("RemovedList"));

        Assert.Contains("CONFLICT, the winner loses A.esp (Entries)", added);
        Assert.Contains($"A.esp: vs Skyrim.esm: +{{Data: {{Level: 1, Reference: {_fixture.AItem} AItem, Count: 1}}}}  LOST (all of it)", added);
        Assert.Contains($"B.esp (winner): vs Skyrim.esm: -{{Data: {{Level: 1, Reference: {_fixture.ItmSword} ItmSword, Count: 1}}}}", added);
        Assert.Contains($"A.esp: vs Skyrim.esm: -{{Data: {{Level: 1, Reference: {_fixture.ItmSword} ItmSword, Count: 1}}}}  LOST (all of it)", removed);

        Assert.Contains("resolved by the winner", Text(_queries.CompareRecord("PatchedSword")));
        Assert.Contains("A.esp (winner, ITM)", Text(_queries.CompareRecord("ItmSword")));
        Assert.Contains("DELETED", Text(_queries.CompareRecord(_fixture.DeletedSword.ToString())));
        Assert.Contains("overridden, no edits lost", Text(_queries.CompareRecord("CleanPotion")));

        var onlyName = Text(_queries.CompareRecord("LostSword", ["Name"]));
        Assert.Contains("Name:", onlyName);
        Assert.DoesNotContain("BasicStats:", onlyName);
    }

    [Fact]
    public void Conflicts_are_summarised_by_lost_field_and_plugins_then_listed_by_group()
    {
        var types = new[] { "Weapon", "LeveledItem", "Ingestible" };

        var summary = _queries.FindConflicts(new ConflictFilter(types));
        Assert.Equal([["LeveledItem.Entries: A.esp → B.esp", "2"], ["Weapon.BasicStats: A.esp → B.esp", "1"]], summary.Rows);

        var group = _queries.FindConflicts(new ConflictFilter(types, Group: "LeveledItem.Entries: A.esp → B.esp"));
        Assert.Equal(["AddedList", "RemovedList"], Column(group, "EditorID").Order());

        Assert.Equal([["Weapon.BasicStats: A.esp → Patch.esp", "1"]], _queries.FindConflicts(new ConflictFilter(types, Status: ConflictKind.Resolved)).Rows);
        Assert.Equal(2, _queries.FindConflicts(new ConflictFilter(types, Status: ConflictKind.Lost, Field: "BasicStats")).Rows.Count);
        Assert.Equal(["ItmSword"], Column(_queries.FindConflicts(new ConflictFilter(Plugin: ConflictFixture.A, Status: ConflictKind.Itm)), "EditorID"));

        var all = _queries.FindConflicts(new ConflictFilter(types, Status: ConflictKind.All));
        var statuses = all.Rows.ToDictionary(r => r[1] + " " + r[0], r => r[3]);
        Assert.Equal("Override, deleted", statuses[$"Weapon {_fixture.DeletedSword}"]);
        Assert.Equal("Itm", statuses[$"Weapon {_fixture.ItmSword}"]);
        Assert.Equal("Override", statuses[$"Ingestible {_fixture.CleanPotion}"]);
        Assert.Equal("Resolved", statuses[$"Weapon {_fixture.PatchedSword}"]);

        Assert.Throws<SafePatchException>(() => _queries.FindConflicts(new ConflictFilter()));
    }

    [Fact]
    public void References_go_both_ways_by_winning_version()
    {
        var aItem = _queries.References("AItem", LinkDirection.In, ["LeveledItem"]);
        var patchList = _queries.References("PatchList", LinkDirection.Out);

        // The winning AddedList (B's) dropped AItem; only the patch's list still links to it.
        Assert.Equal(["PatchList"], Column(aItem, "EditorID"));
        Assert.Equal(["AItem", "BItem"], Column(patchList, "EditorID").Order());
        Assert.Equal(["PatchList"], Column(_queries.References("AItem", LinkDirection.In), "EditorID"));
    }

    [Fact]
    public void An_asset_shows_every_provider_the_winner_first_and_the_records_using_it()
    {
        const string mesh = @"meshes\safepatch\sword.nif";
        BsaFixture.Write(Path.Combine(Data, "A.bsa"), new Dictionary<string, byte[]> { [mesh] = "archive"u8.ToArray(), [@"meshes\safepatch\other.nif"] = [1] });
        Directory.CreateDirectory(Path.Combine(Data, "meshes", "safepatch"));
        File.WriteAllText(Path.Combine(Data, mesh), "loose");
        var assets = new SkyrimMod(ModKey.FromFileName("Assets.esp"), SkyrimRelease.SkyrimSE);
        assets.Weapons.AddNew("ModelSword").Model = new Model { File = @"safepatch\sword.nif" };
        assets.WriteToBinary(Path.Combine(Data, "Assets.esp"));
        File.AppendAllLines(_pluginsFile, ["*Assets.esp"]);
        using var snapshot = LoadOrderSnapshot.Open(Data, _pluginsFile);
        var queries = new QueryService(snapshot);

        var set = queries.FindAsset("Meshes/SafePatch/Sword.nif", ["Weapon"]);
        var archiveOnly = queries.FindAsset(@"meshes\safepatch\other.nif");

        Assert.Equal(["wins", "provides", "used by"], Column(set, "Role"));
        Assert.Equal(["loose", "archive"], Column(set, "Kind").Take(2));
        Assert.EndsWith("A.bsa", set.Rows[1][3]);
        Assert.Equal("ModelSword", set.Rows[2][2]);
        Assert.Equal(["wins"], Column(archiveOnly, "Role"));
        Assert.Contains(@"Assets: meshes\safepatch\sword.nif", Text(queries.GetRecord("ModelSword")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No loose file or loaded archive has it.", Text(queries.FindAsset(@"meshes\none.nif")));
        Assert.Throws<SafePatchException>(() => queries.FindAsset(@"..\secret.txt"));
    }

    [Fact]
    public void A_result_warns_once_the_load_order_on_disk_changes()
    {
        Assert.Null(_queries.LoadOrder().Notes!.FirstOrDefault(n => n.StartsWith("Warning")));

        File.SetLastWriteTimeUtc(_pluginsFile, DateTime.UtcNow.AddMinutes(1));

        Assert.StartsWith("Warning: plugins.txt changed", _queries.LoadOrder().Notes![0]);
    }

    public void Dispose()
    {
        _snapshot.Dispose();
        _temp.Dispose();
    }
}
