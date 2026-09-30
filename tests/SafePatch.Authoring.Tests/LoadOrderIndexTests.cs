using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Index;
using SafePatch.Host;
using SafePatch.Mutagen;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>
/// The raw index and record reader must agree with Mutagen exactly: the same records, versions and EditorIDs, and
/// every version decoded to what Mutagen reads.
/// </summary>
public sealed class LoadOrderIndexTests : IDisposable
{
    private readonly TempFolder _temp = new("SafePatchIndex-");

    private string Data => _temp.File("Data");

    /// <summary>Every record version Mutagen enumerates, by FormKey: the plugins holding one, in load order.</summary>
    private static Dictionary<FormKey, List<ModKey>> MutagenChains(LoadOrderSnapshot snapshot)
    {
        var chains = new Dictionary<FormKey, List<ModKey>>();
        foreach (var mod in snapshot.Mods)
        {
            foreach (var record in mod.EnumerateMajorRecords())
            {
                if (!chains.TryGetValue(record.FormKey, out var plugins)) chains[record.FormKey] = plugins = [];
                if (plugins.LastOrDefault() != mod.ModKey) plugins.Add(mod.ModKey);
            }
        }
        return chains;
    }

    private static void AssertAgreesWithMutagen(LoadOrderSnapshot snapshot)
    {
        var index = snapshot.Index;
        var expected = MutagenChains(snapshot);

        Assert.Equal(expected.Count, index.Count);
        foreach (var chain in index.Chains)
        {
            Assert.Equal(expected[chain.FormKey], chain.Plugins.Select(p => index.Plugins[p]));
            for (var v = 0; v < chain.Versions; v++)
            {
                var plugin = snapshot.Mods[chain.PluginAt(v)];
                var mutagen = plugin.EnumerateMajorRecords().First(r => r.FormKey == chain.FormKey);
                var decoded = snapshot.Read(chain, v);
                Assert.NotNull(decoded);
                Assert.Empty(RecordDiff.ChangedFields(mutagen, decoded));
                if (v == chain.Versions - 1 && mutagen.EditorID is { } editorId) Assert.Equal(editorId, chain.EditorId);
            }
        }
    }

    [Fact]
    public void The_index_and_reader_agree_with_Mutagen_on_nested_records_and_overrides()
    {
        PluginFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, PluginFixture.WriteLoadOrder(_temp.File("plugins.txt")));

        AssertAgreesWithMutagen(snapshot);
    }

    [Fact]
    public void A_search_for_one_record_type_finds_those_nested_in_other_types_groups()
    {
        var fixture = PluginFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, PluginFixture.WriteLoadOrder(_temp.File("plugins.txt")));

        // Placed objects live in the cell and worldspace groups, which a search for them must not skip.
        var placed = snapshot.Index.WinnersMentioning(fixture.Sword, SafePatch.Authoring.Query.RecordTypes.Signatures("PlacedObject").ToHashSet());
        Assert.Equal(new[] { fixture.InteriorRef, fixture.ExteriorRef, fixture.PersistentRef }.Order(), placed.Select(c => c.FormKey).Order());
    }

    [Fact]
    public void The_index_and_reader_agree_with_Mutagen_on_every_kind_of_conflict()
    {
        var (_, plugins) = ConflictFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, plugins);

        AssertAgreesWithMutagen(snapshot);
        Assert.True(snapshot.Index.Find(snapshot.Index.FindEditorId("DeletedSword")!.Value.FormKey)!.Value.WinnerDeleted);
    }

    [Fact]
    public void Light_and_compressed_plugins_are_read_the_way_Mutagen_reads_them()
    {
        var (_, plugins) = ConflictFixture.Write(Data);
        var light = new SkyrimMod(ModKey.FromFileName("Light.esp"), SkyrimRelease.SkyrimSE);
        light.ModHeader.Flags |= (SkyrimModHeader.HeaderFlag)0x200;
        var sword = light.Weapons.AddNew("LightSword");
        sword.Name = "Compressed";
        sword.IsCompressed = true;
        using (var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Data, "Skyrim.esm"), SkyrimRelease.SkyrimSE))
        {
            var potion = (Ingestible)skyrim.Ingestibles.First().DeepCopy();
            potion.Value = 99;
            potion.IsCompressed = true;
            light.Ingestibles.Set(potion);
        }
        light.WriteToBinary(Path.Combine(Data, "Light.esp"));
        File.AppendAllLines(plugins, ["*Light.esp"]);
        using var snapshot = LoadOrderSnapshot.Open(Data, plugins);

        Assert.Equal(2, PluginScanner.Scan(Path.Combine(Data, "Light.esp")).Records.Count(r => r.IsCompressed));
        AssertAgreesWithMutagen(snapshot);
        Assert.Equal(sword.FormKey, snapshot.Index.FindEditorId("lightsword")!.Value.FormKey);
    }

    [Fact]
    public void Records_are_found_by_FormKey_and_by_EditorID_in_any_case()
    {
        var (fixture, plugins) = ConflictFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, plugins);
        var index = snapshot.Index;

        Assert.Equal(fixture.LostSword, index.FindEditorId("LOSTSWORD")!.Value.FormKey);
        Assert.Equal("LostSword", index.Find(fixture.LostSword)!.Value.EditorId);
        Assert.Null(index.FindEditorId("NoSuchRecord"));
        Assert.Null(index.Find(FormKey.Factory("123456:Nowhere.esp")));
        Assert.Equal([0, 1, 2, 3], index.Find(fixture.PatchedSword)!.Value.Plugins);
    }

    [Fact]
    public void A_raw_search_finds_winning_versions_that_mention_a_record()
    {
        var (fixture, plugins) = ConflictFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, plugins);

        // A's AddedList mentions AItem, but B's version wins and does not.
        Assert.Equal(["PatchList"], snapshot.Index.WinnersMentioning(fixture.AItem).Select(c => c.EditorId));
        Assert.Equal(["PatchList", "RemovedList"], snapshot.Index.WinnersMentioning(fixture.BItem).Select(c => c.EditorId).Order());
    }

    [Fact]
    public void Plugins_can_be_deleted_while_indexed_and_read()
    {
        var (_, plugins) = ConflictFixture.Write(Data);
        using var snapshot = LoadOrderSnapshot.Open(Data, plugins);
        var chain = snapshot.Index.FindEditorId("AItem")!.Value;
        Assert.NotNull(snapshot.Read(chain, 0));

        File.Delete(Path.Combine(Data, ConflictFixture.A));

        Assert.False(File.Exists(Path.Combine(Data, ConflictFixture.A)));
    }

    [Theory]
    [InlineData(0u)]   // a group that does not advance: Mutagen loops forever on it
    [InlineData(10u)]  // shorter than a group header
    [InlineData(999999u)] // longer than the file
    public void Malformed_framing_is_refused(uint groupSize)
    {
        var mod = new SkyrimMod(ModKey.FromFileName("Bad.esp"), SkyrimRelease.SkyrimSE);
        mod.Weapons.AddNew("Sword");
        var path = _temp.File("Bad.esp");
        mod.WriteToBinary(path);
        var bytes = File.ReadAllBytes(path);
        var group = bytes.AsSpan().IndexOf("GRUP"u8);
        BitConverter.TryWriteBytes(bytes.AsSpan(group + 4), groupSize);

        Assert.Throws<SafePatchException>(() => PluginScanner.Scan("Bad.esp", bytes));
    }

    public void Dispose() => _temp.Dispose();
}
