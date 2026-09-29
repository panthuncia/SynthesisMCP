using System.Text.Json.Nodes;

namespace SafePatch.EndToEnd.Tests;

/// <summary>Drives the real Synthesis CLI: one profile with one group of solution patchers.</summary>
public sealed class SynthesisCli(E2ETools tools, Workspace workspace)
{
    public const string Profile = "SafePatchE2E";
    public const string Group = "SafePatchGroup";

    /// <summary>The plugin Synthesis writes for the group.</summary>
    public string OutputPlugin => Path.Combine(workspace.OutputFolder, $"{Group}.esp");

    public void CreateProfile(params string[] solutions)
    {
        Check(Processes.Run(tools.SynthesisCli,
            ["create-profile", "-s", workspace.PipelineSettings, "-r", "SkyrimSE", "-n", Profile, "-g", Group], workspace.Root));
        foreach (var solution in solutions)
        {
            var project = Path.GetFileNameWithoutExtension(solution);
            Check(Processes.Run(tools.SynthesisCli,
            [
                "add-solution-patcher", "-s", workspace.PipelineSettings, "-p", Profile, "-g", Group,
                "--PatcherNickname", project, "--SolutionPath", solution, "--ProjectSubpath", Path.Combine(project, $"{project}.csproj"),
            ], workspace.Root));
        }
    }

    /// <summary>Sets profile options the CLI has no switch for, such as <c>Localize</c>, in the pipeline settings file.</summary>
    public void SetProfileOption(string name, bool value)
    {
        var settings = JsonNode.Parse(File.ReadAllText(workspace.PipelineSettings))!;
        var profile = settings["Profiles"]!.AsArray().Single(p => (string?)p!["Nickname"] == Profile)!;
        profile[name] = value;
        File.WriteAllText(workspace.PipelineSettings, settings.ToJsonString());
    }

    /// <summary>Arguments for <c>run-pipeline</c>, so the same run can be launched directly or through MO2.</summary>
    public IReadOnlyList<string> RunPipelineArguments(string dataFolder, string loadOrderFile) =>
    [
        "run-pipeline", "-s", workspace.PipelineSettings, "-p", Profile,
        "-o", workspace.OutputFolder, "-d", dataFolder, "-l", loadOrderFile,
    ];

    /// <summary>
    /// Where Synthesis keeps a patcher's user data, such as its saved settings: <c>Data\&lt;profile&gt;\&lt;patcher&gt;</c>
    /// under the CLI's working directory (it does not apply <c>--ExtraDataFolder</c> to patchers).
    /// </summary>
    public string PatcherDataFolder(string patcher) => Path.Combine(workspace.Root, "Data", Profile, patcher);

    public ProcessResult RunPipeline(string dataFolder, string loadOrderFile) =>
        Processes.Run(tools.SynthesisCli, RunPipelineArguments(dataFolder, loadOrderFile), workingDirectory: workspace.Root);

    private static void Check(ProcessResult result) => Assert.True(result.ExitCode == 0, result.ToString());
}
