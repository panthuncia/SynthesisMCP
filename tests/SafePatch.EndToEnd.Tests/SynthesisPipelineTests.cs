using Mutagen.Bethesda.Skyrim;
using SafePatch.Generator;
using SafePatch.TestSupport;

namespace SafePatch.EndToEnd.Tests;

/// <summary>
/// Runs generated patchers through the real Synthesis CLI (built from the pinned source): Synthesis
/// builds the generated solution, runs it, and its worker runs in the real sandbox.
/// </summary>
[Trait("Category", "Synthesis")]
public sealed class SynthesisPipelineTests : IDisposable
{
    /// <summary>Renames the list, which the manifest (entries only) does not allow.</summary>
    public const string InvalidProgram = """
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class Renamer
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides())
                    list.GetOrAddAsOverride(state.PatchMod).EditorID = "Renamed";
            }
        }
        """;

    private readonly Workspace _workspace = new();

    [Fact]
    public void Merges_the_conflict_and_a_following_patcher_in_the_group_sees_it()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        cli.CreateProfile(_workspace.GenerateSafePatcher("CacoMerge", SamplePrograms.LeveledListMerge), _workspace.GenerateVisibilityProbe());

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("SafePatch 'CacoMerge': 1 record change(s)", run.Output);
        Assert.Contains("Restored 1 entries to LItemSafePatchTest", run.Output);
        AssertMerged(_workspace, cli.OutputPlugin);
        AssertProbeSawMerge(_workspace);
    }

    [Fact]
    public void A_patcher_built_against_the_packed_runtime_merges_the_conflict()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        var solution = _workspace.GenerateSafePatcher("PackedMerge", SamplePrograms.LeveledListMerge,
            s => s with { Runtime = new RuntimeReference.Package(LocalFeed.Version) });
        LocalFeed.Configure(Path.GetDirectoryName(solution)!);
        cli.CreateProfile(solution);

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("SafePatch 'PackedMerge': 1 record change(s)", run.Output);
        AssertMerged(_workspace, cli.OutputPlugin);
        // The package's buildTransitive targets put the worker in its own folder, not beside the patcher.
        var bin = Directory.GetFiles(Path.GetDirectoryName(solution)!, "PackedMerge.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Single(d => d!.Contains(Path.Combine("bin", ""), StringComparison.OrdinalIgnoreCase))!;
        Assert.True(File.Exists(Path.Combine(bin, "SafePatch.Worker", "SafePatch.Worker.exe")), bin);
        Assert.False(File.Exists(Path.Combine(bin, "SafePatch.Worker.exe")));
    }

    [Fact]
    public void A_rejected_proposal_fails_the_pipeline_and_writes_no_plugin()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        cli.CreateProfile(_workspace.GenerateSafePatcher("Invalid", InvalidProgram));

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode != 0, run.ToString());
        Assert.Contains("changing LeveledItem.EditorID is not allowed", run.Output);
        Assert.False(File.Exists(cli.OutputPlugin));
    }

    [Fact]
    public void When_Synthesis_cannot_write_the_output_after_a_commit_the_run_fails_and_the_old_file_stays()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        cli.CreateProfile(_workspace.GenerateSafePatcher("CacoMerge", SamplePrograms.LeveledListMerge));
        Directory.CreateDirectory(Path.GetDirectoryName(cli.OutputPlugin)!);
        File.WriteAllText(cli.OutputPlugin, "previous output");
        File.SetAttributes(cli.OutputPlugin, FileAttributes.ReadOnly);
        try
        {
            var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);
            TestContext.Current.TestOutputHelper?.WriteLine(run.Output);

            Assert.True(run.ExitCode != 0, run.ToString());
            Assert.Contains("SafePatch 'CacoMerge': 1 record change(s)", run.Output);
            Assert.Equal("previous output", File.ReadAllText(cli.OutputPlugin));
        }
        finally
        {
            File.SetAttributes(cli.OutputPlugin, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_localized_profile_gets_a_localized_plugin_with_the_programs_changes()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        const string namer = """
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Namer
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) =>
                    state.PatchMod.Weapons.AddNew("LocalizedSword").Name = "Épée longue";
            }
            """;
        cli.CreateProfile(_workspace.GenerateSafePatcher("Namer", namer, s => s with { Writable = [], Creatable = ["Weapon"] }));
        cli.SetProfileOption("Localize", true);

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode == 0, run.ToString());
        using var output = SkyrimMod.CreateFromBinaryOverlay(cli.OutputPlugin, SkyrimRelease.SkyrimSE);
        Assert.True(output.UsingLocalization, run.Output);
        Assert.Equal("Épée longue", Assert.Single(output.Weapons).Name?.String);
    }

    [Fact]
    public void Saved_settings_reach_the_sandboxed_program()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        cli.CreateProfile(_workspace.GenerateSafePatcher("Named", SamplePrograms.NamedList,
            s => s with { SettingsSource = SamplePrograms.NamingSettings, Writable = [], Creatable = ["LeveledItem"] }));
        Directory.CreateDirectory(cli.PatcherDataFolder("Named"));
        File.WriteAllText(Path.Combine(cli.PatcherDataFolder("Named"), "settings.json"), """{ "EditorId": "LItemFromSynthesisSettings" }""");

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode == 0, run.ToString());
        using var output = SkyrimMod.CreateFromBinaryOverlay(cli.OutputPlugin, SkyrimRelease.SkyrimSE);
        Assert.True(Assert.Single(output.LeveledItems).EditorID == "LItemFromSynthesisSettings", run.Output);
    }

    [Fact]
    public void A_loose_asset_the_manifest_allows_is_read_through_the_asset_provider()
    {
        var cli = new SynthesisCli(E2ETools.Require(), _workspace);
        _workspace.WritePlugins(_workspace.DataFolder);
        var loadOrder = _workspace.WriteLoadOrder(Path.Combine(_workspace.Root, "plugins.txt"));
        Directory.CreateDirectory(Path.Combine(_workspace.DataFolder, "meshes", "safepatch"));
        File.WriteAllText(Path.Combine(_workspace.DataFolder, "meshes", "safepatch", "thing.nif"), "NIF");
        const string reader = """
            using System.IO;
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Assets;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class AssetReader
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    var name = "LItemMissing";
                    if (state.AssetProvider.TryGetStream(new DataRelativePath(@"meshes\safepatch\thing.nif"), out var stream))
                        using (stream) name = "LItem" + new StreamReader(stream).ReadToEnd();
                    state.PatchMod.LeveledItems.AddNew(name);
                }
            }
            """;
        cli.CreateProfile(_workspace.GenerateSafePatcher("AssetReader", reader,
            s => s with { Writable = [], Creatable = ["LeveledItem"], Assets = ["meshes/**/*.nif"] }));

        var run = cli.RunPipeline(_workspace.DataFolder, loadOrder);

        Assert.True(run.ExitCode == 0, run.ToString());
        using var output = SkyrimMod.CreateFromBinaryOverlay(cli.OutputPlugin, SkyrimRelease.SkyrimSE);
        Assert.Equal("LItemNIF", Assert.Single(output.LeveledItems).EditorID);
    }

    internal static void AssertMerged(Workspace workspace, string outputPlugin)
    {
        Assert.True(File.Exists(outputPlugin), $"{outputPlugin} was not written.");
        using var output = SkyrimMod.CreateFromBinaryOverlay(outputPlugin, SkyrimRelease.SkyrimSE);
        var list = Assert.Single(output.LeveledItems);
        var references = list.Entries!.Select(e => e.Data!.Reference.FormKey).ToList();
        Assert.Contains(workspace.Plugins!.CacoPotion, references);
        Assert.Contains(workspace.Plugins.OtherArmor, references);
    }

    internal static void AssertProbeSawMerge(Workspace workspace)
    {
        var probe = workspace.ReadProbe();
        Assert.Contains(workspace.Plugins!.CacoPotion.ToString(), probe["loadorder"]);
        Assert.Contains(workspace.Plugins.CacoPotion.ToString(), probe["linkcache"]);
    }

    public void Dispose() => _workspace.Dispose();
}
