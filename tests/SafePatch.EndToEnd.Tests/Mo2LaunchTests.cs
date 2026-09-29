using SafePatch.TestSupport;

namespace SafePatch.EndToEnd.Tests;

/// <summary>
/// Launches the Synthesis CLI through Mod Organizer 2, as users do. The conflicting plugins exist
/// only in an MO2 mod folder, so Synthesis can only see them through MO2's virtual file system,
/// and the SafePatch worker is started (sandboxed) from inside that hooked process tree.
/// </summary>
[Trait("Category", "MO2")]
public sealed class Mo2LaunchTests : IDisposable
{
    private const string ModName = "Conflicting Mods";
    private readonly Workspace _workspace = new();

    [Fact]
    public void Synthesis_run_through_MO2_patches_plugins_from_the_virtual_data_folder()
    {
        var tools = E2ETools.Require();
        Mo2Instance.RequireNoOtherVfs();
        var mo2 = new Mo2Instance(tools, _workspace);
        _workspace.WritePlugins(mo2.ModFolder(ModName));
        mo2.SetProfile([ModName], [Workspace.Caco, Workspace.Other]);
        var cli = new SynthesisCli(tools, _workspace);
        cli.CreateProfile(_workspace.GenerateSafePatcher("CacoMerge", SamplePrograms.LeveledListMerge), _workspace.GenerateVisibilityProbe());
        // Compiling under MO2's VFS crashes csc (a known Synthesis/MO2 problem), so, as users do, let
        // Synthesis build the patchers outside MO2 first; the MO2 run then reuses those builds.
        var warmUp = cli.RunPipeline(_workspace.DataFolder, _workspace.WriteEmptyLoadOrder());
        Assert.True(warmUp.ExitCode == 0, warmUp.ToString());
        File.Delete(cli.OutputPlugin);
        File.Delete(_workspace.ProbeReport);

        var run = mo2.Run(tools.SynthesisCli, cli.RunPipelineArguments(_workspace.DataFolder, mo2.PluginsFile), TimeSpan.FromMinutes(10));

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("SafePatch 'CacoMerge': 1 record change(s)", run.Output);
        SynthesisPipelineTests.AssertMerged(_workspace, cli.OutputPlugin);
        SynthesisPipelineTests.AssertProbeSawMerge(_workspace);
        // The plugins were only ever virtual.
        Assert.False(File.Exists(Path.Combine(_workspace.DataFolder, Workspace.Caco)));
    }

    public void Dispose() => _workspace.Dispose();
}
