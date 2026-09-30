using SafePatch.Authoring.Query;
using SafePatch.Authoring.Sources;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>
/// Load orders from a plain Data folder and from an MO2 profile (a fake instance on disk; MO2 itself never runs).
/// Both must read the same load order the same way.
/// </summary>
public sealed class LoadOrderSourceTests : IDisposable
{
    /// <summary>Creates a LeveledItem named after the contents of an MO2-layered loose file.</summary>
    private const string MeshReader = """
        using System.IO;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Assets;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class MeshReader
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                var name = "LItemMissing";
                if (state.AssetProvider.TryGetStream(new DataRelativePath(@"meshes\mod.nif"), out var stream))
                    using (stream) name = "LItem" + new StreamReader(stream).ReadToEnd();
                state.PatchMod.LeveledItems.AddNew(name);
            }
        }
        """;

    private readonly TempFolder _temp = new("SafePatchSources-");
    private readonly Mo2Fixture _mo2;

    public LoadOrderSourceTests() => _mo2 = Mo2Fixture.Write(_temp.Path);

    private Mo2ProfileSource Source => new(_mo2.Instance);

    [Fact]
    public void An_MO2_profile_layers_the_game_the_enabled_mods_and_overwrite()
    {
        var resolved = Source.Resolve();

        Assert.Equal(["Data", Mo2Fixture.CacoMod, Mo2Fixture.OtherMod, "overwrite"], resolved.View.Layers.Select(l => l.Name));
        Assert.Equal(["overwrite", Mo2Fixture.OtherMod, Mo2Fixture.CacoMod, "Data"], resolved.View.Providers(Mo2Fixture.SharedTexture).Select(f => f.Layer.Name));
        Assert.Equal(Mo2Fixture.OtherMod, resolved.View.Find(Mo2Fixture.ModMesh)!.Layer.Name);
        Assert.Equal(Mo2Fixture.CacoMod, resolved.View.TopFiles[PluginFixture.Caco].Layer.Name);
        Assert.Equal(Path.Combine(_mo2.ProfileFolder, "plugins.txt"), resolved.PluginsFile);
        Assert.Equal(_mo2.DataFolder, resolved.View.DataFolder);
        Assert.Empty(resolved.Warnings);
    }

    [Fact]
    public void An_MO2_profile_reads_the_same_load_order_as_its_flattened_Data_folder()
    {
        var (data, plugins) = _mo2.Flatten(Path.Combine(_temp.Path, "Flat"));
        using var fromMo2 = LoadOrderSnapshot.Open(Source);
        using var flat = LoadOrderSnapshot.Open(data, plugins);
        var mo2Queries = new QueryService(fromMo2);
        var flatQueries = new QueryService(flat);

        var rows = mo2Queries.LoadOrder().Rows;
        Assert.Equal([PluginFixture.BaseMaster, PluginFixture.Caco, PluginFixture.Other], rows.Select(p => p[1]));
        Assert.Equal(flatQueries.LoadOrder().Rows, rows);
        Assert.Equal(flatQueries.CompareRecord(PluginFixture.ListEditorId).Rows, mo2Queries.CompareRecord(PluginFixture.ListEditorId).Rows);
    }

    [Fact]
    public void An_open_snapshot_does_not_stop_a_mod_manager_from_changing_plugins()
    {
        using var snapshot = LoadOrderSnapshot.Open(Source);
        var queries = new QueryService(snapshot);
        var before = queries.CompareRecord(PluginFixture.ListEditorId).Rows;

        // Deleting a mod, and renaming a plugin away to put a new one in its place (as xEdit saves).
        Directory.Delete(_mo2.ModFolder(Mo2Fixture.CacoMod), recursive: true);
        var other = Path.Combine(_mo2.ModFolder(Mo2Fixture.OtherMod), PluginFixture.Other);
        File.Move(other, other + ".backup");
        File.Copy(other + ".backup", other);

        Assert.Equal(before, queries.CompareRecord(PluginFixture.ListEditorId).Rows);
        Assert.NotNull(snapshot.StaleReason());
    }

    [Fact]
    public void A_snapshot_is_stale_once_the_profile_changes_and_reopening_reads_it_again()
    {
        using var snapshot = LoadOrderSnapshot.Open(Source);
        Assert.Null(snapshot.StaleReason());

        var modList = Path.Combine(_mo2.ProfileFolder, "modlist.txt");
        File.WriteAllLines(modList, File.ReadAllLines(modList).Select(l => l == $"+{Mo2Fixture.OtherMod}" ? $"-{Mo2Fixture.OtherMod}" : l));
        File.SetLastWriteTimeUtc(modList, DateTime.UtcNow.AddMinutes(1));

        Assert.Contains("modlist.txt", snapshot.StaleReason());
        using var reopened = snapshot.Reopen();
        Assert.NotEqual(snapshot.Generation, reopened.Generation);
        Assert.Null(reopened.StaleReason());
        Assert.DoesNotContain(reopened.View.Layers, l => l.Name == Mo2Fixture.OtherMod);
    }

    [Fact]
    public void The_profile_defaults_to_the_selected_one_and_a_missing_one_is_refused()
    {
        Assert.Contains(Mo2Fixture.Profile, Source.Resolve().Description);
        Assert.Throws<SafePatchException>(() => new Mo2ProfileSource(_mo2.Instance, "NoSuchProfile").Resolve());
        Assert.Throws<SafePatchException>(() => new Mo2ProfileSource(_mo2.GameFolder).Resolve());
    }

    [Fact]
    public void An_enabled_mod_without_a_folder_is_reported()
    {
        File.AppendAllLines(Path.Combine(_mo2.ProfileFolder, "modlist.txt"), ["+Deleted Mod"]);

        Assert.Contains(Source.Resolve().Warnings, w => w.Contains("Deleted Mod"));
    }

    [Fact]
    public void Profile_specific_game_INI_files_come_from_the_profile()
    {
        File.WriteAllText(Path.Combine(_mo2.ProfileFolder, "settings.ini"), "[General]\nLocalSettings=true\n");

        Assert.Equal(_mo2.ProfileFolder, Path.GetDirectoryName(Source.Resolve().GameIni));
    }

    [Fact]
    public void A_test_run_against_an_MO2_profile_sees_plugins_and_loose_files_from_mod_folders()
    {
        using var snapshot = LoadOrderSnapshot.Open(Source);
        var service = new AuthoringService(snapshot, InProcessWorkerLauncher.Real());

        var merge = service.TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), gameIni: "", cancel: TestContext.Current.CancellationToken);
        var mesh = service.TestPatch(MeshReader, new PatchScope([], Creatable: ["LeveledItem"], Assets: ["meshes/**/*.nif"]), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.True(merge.Accepted, merge.Error);
        Assert.Single(merge.Changes);
        Assert.True(mesh.Accepted, mesh.Error);
        Assert.Equal("LItemother", Assert.Single(mesh.Changes).Change.EditorId);
    }

    [Fact]
    [Trait("Category", "Sandbox")]
    public void A_sandboxed_test_run_against_an_MO2_profile_sees_the_layered_Data_folder()
    {
        using var snapshot = LoadOrderSnapshot.Open(Source);

        var mesh = new AuthoringService(snapshot).TestPatch(MeshReader, new PatchScope([], Creatable: ["LeveledItem"], Assets: ["meshes/**/*.nif"]),
            gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.True(mesh.Accepted, mesh.Error);
        Assert.Equal("LItemother", Assert.Single(mesh.Changes).Change.EditorId);
    }

    [Fact]
    public void A_loose_file_in_several_MO2_mods_comes_from_the_highest_priority_one()
    {
        using var snapshot = LoadOrderSnapshot.Open(Source);

        var shared = new QueryService(snapshot).FindAsset(Mo2Fixture.SharedTexture);
        var mesh = new QueryService(snapshot).FindAsset(Mo2Fixture.ModMesh);

        Assert.Equal(["overwrite", Mo2Fixture.OtherMod, Mo2Fixture.CacoMod, "Data"], shared.Rows.Select(r => r[2]));
        Assert.Equal(["wins", "provides", "provides", "provides"], shared.Rows.Select(r => r[0]));
        Assert.Equal([Mo2Fixture.OtherMod, Mo2Fixture.CacoMod], mesh.Rows.Select(r => r[2]));
    }

    public void Dispose() => _temp.Dispose();
}
