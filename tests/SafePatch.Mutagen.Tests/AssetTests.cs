using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// <c>state.AssetProvider</c> is Synthesis's own, over the worker's file system; loose files are
/// fetched from the host on demand, within the manifest's asset patterns.
/// </summary>
public class AssetTests
{
    /// <summary>Names a new leveled list after what the asset provider reports for <paramref name="path"/>.</summary>
    private static string Probe(string path) => $$"""
        using System.IO;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Assets;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class P
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                var path = new DataRelativePath(@"{{path}}");
                var name = "LItemMissing";
                if (state.AssetProvider.TryGetSize(path, out var size) && state.AssetProvider.TryGetStream(path, out var stream))
                {
                    using (stream) name = "LItem" + size + "_" + new StreamReader(stream).ReadToEnd();
                }
                state.PatchMod.LeveledItems.AddNew(name);
            }
        }
        """;

    private static void WriteAsset(HostRun run, string path, string contents)
    {
        var full = Path.Combine(run.DataFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    [Fact]
    public void A_program_reads_a_loose_asset_the_manifest_allows()
    {
        using var run = new HostRun();
        WriteAsset(run, @"meshes\safepatch\thing.nif", "NIF");

        var report = run.Run(Probe(@"meshes\safepatch\thing.nif"), creatable: ["LeveledItem"], assets: ["meshes/**/*.nif"]);

        Assert.Equal("LItem3_NIF", Assert.Single(report.Changes).EditorId);
        Assert.Empty(report.DeniedAssets!);
    }

    [Fact]
    public void An_asset_outside_the_manifest_reads_as_missing_and_is_reported()
    {
        using var run = new HostRun();
        WriteAsset(run, @"textures\safepatch\thing.dds", "DDS");

        var report = run.Run(Probe(@"textures\safepatch\thing.dds"), creatable: ["LeveledItem"], assets: ["meshes/**"]);

        Assert.Equal("LItemMissing", Assert.Single(report.Changes).EditorId);
        Assert.Contains(@"textures\safepatch\thing.dds", report.DeniedAssets!);
    }

    [Fact]
    public void Without_asset_patterns_no_loose_file_is_readable()
    {
        using var run = new HostRun();
        WriteAsset(run, @"meshes\safepatch\thing.nif", "NIF");

        var report = run.Run(Probe(@"meshes\safepatch\thing.nif"), creatable: ["LeveledItem"]);

        Assert.Equal("LItemMissing", Assert.Single(report.Changes).EditorId);
    }

    private static void WriteArchive(HostRun run, string archive, string path, string contents) =>
        BsaFixture.Write(Path.Combine(run.DataFolder, archive), new Dictionary<string, byte[]> { [path] = System.Text.Encoding.ASCII.GetBytes(contents) });

    [Fact]
    public void A_file_in_an_archive_named_after_a_plugin_is_read()
    {
        using var run = new HostRun();
        WriteArchive(run, "OtherMod.bsa", @"meshes\safepatch\packed.nif", "BSA");

        var report = run.Run(Probe(@"meshes\safepatch\packed.nif"), creatable: ["LeveledItem"], assets: ["meshes/**"]);

        Assert.Equal("LItem3_BSA", Assert.Single(report.Changes).EditorId);
    }

    [Theory]
    [InlineData(true, "LItem3_BSA")]
    [InlineData(false, "LItemMissing")]
    public void A_file_in_an_archive_only_the_game_ini_lists_is_read_through_the_shared_ini(bool listed, string expected)
    {
        using var run = new HostRun();
        WriteArchive(run, "Custom - Packed.bsa", @"meshes\safepatch\packed.nif", "BSA");
        File.WriteAllLines(run.GameIni, ["[Archive]", listed ? "sResourceArchiveList=Other.bsa, Custom - Packed.bsa" : "sResourceArchiveList=Other.bsa"]);

        var report = run.Run(Probe(@"meshes\safepatch\packed.nif"), creatable: ["LeveledItem"], assets: ["meshes/**"]);

        Assert.Equal(expected, Assert.Single(report.Changes).EditorId);
    }

    [Fact]
    public void A_loose_file_wins_over_the_same_file_in_an_archive()
    {
        using var run = new HostRun();
        WriteArchive(run, "OtherMod.bsa", @"meshes\safepatch\thing.nif", "BSA");
        WriteAsset(run, @"meshes\safepatch\thing.nif", "NIF");

        var report = run.Run(Probe(@"meshes\safepatch\thing.nif"), creatable: ["LeveledItem"], assets: ["meshes/**"]);

        Assert.Equal("LItem3_NIF", Assert.Single(report.Changes).EditorId);
    }
}
