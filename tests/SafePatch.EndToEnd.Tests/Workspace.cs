using SafePatch.Generator;
using SafePatch.TestSupport;

namespace SafePatch.EndToEnd.Tests;

/// <summary>
/// A throwaway game install, plugins, patchers and Synthesis settings under one temp folder.
/// Plugins reproduce the standard conflict: CACO adds a potion to a vanilla list and a later
/// mod's override drops it.
/// </summary>
public sealed class Workspace : IDisposable
{
    public const string BaseMaster = PluginFixture.BaseMaster;
    public const string Caco = PluginFixture.Caco;
    public const string Other = PluginFixture.Other;
    public const string ListEditorId = PluginFixture.ListEditorId;

    public Workspace()
    {
        Root = Directory.CreateTempSubdirectory("SafePatchE2E-").FullName;
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(OutputFolder);
        // Mod Organizer's Skyrim SE plugin recognises a game folder by its executable.
        File.WriteAllBytes(Path.Combine(GameFolder, "SkyrimSE.exe"), []);
    }

    public string Root { get; }
    public string GameFolder => Path.Combine(Root, "Game");
    public string DataFolder => Path.Combine(GameFolder, "Data");
    public string OutputFolder => Path.Combine(Root, "Output");
    public string PipelineSettings => Path.Combine(Root, "PipelineSettings.json");
    public string ProbeReport => Path.Combine(Root, "probe.txt");

    public PluginFixture? Plugins { get; private set; }

    /// <summary>Writes Skyrim.esm into the game's Data folder and the two conflicting plugins into <paramref name="pluginFolder"/>.</summary>
    public void WritePlugins(string pluginFolder) => Plugins = PluginFixture.Write(DataFolder, pluginFolder);

    /// <summary>A Skyrim SE plugins.txt enabling both conflicting plugins, CACO first.</summary>
    public string WriteLoadOrder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, ["# SafePatch end-to-end load order", $"*{Caco}", $"*{Other}"]);
        return path;
    }

    /// <summary>A plugins.txt with nothing enabled beyond the base master.</summary>
    public string WriteEmptyLoadOrder()
    {
        var path = Path.Combine(Root, "empty-plugins.txt");
        File.WriteAllLines(path, ["# SafePatch end-to-end empty load order"]);
        return path;
    }

    /// <summary>Generates the SafePatch patcher repository; returns its solution path.</summary>
    /// <param name="configure">Adjusts the spec, e.g. to add settings, creatable types or asset patterns.</param>
    public string GenerateSafePatcher(string name, string source, Func<PatcherSpec, PatcherSpec>? configure = null)
    {
        var directory = Path.Combine(Root, "Patchers", name);
        var spec = new PatcherSpec
        {
            Name = name,
            OutputPlugin = $"{name}.esp",
            Source = source,
            Writable = SamplePrograms.LeveledListMergeWritable,
            Runtime = new RuntimeReference.Project(Path.Combine(RepoRoot, "src", "SafePatch.Synthesis", "SafePatch.Synthesis.csproj")),
        };
        var result = PatcherGenerator.Generate(configure?.Invoke(spec) ?? spec, directory);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Path.Combine(directory, $"{name}.sln");
    }

    /// <summary>
    /// A plain Synthesis patcher that records which entries the winning test list has when it
    /// runs, as seen through both the load order and the link cache.
    /// </summary>
    public string GenerateVisibilityProbe()
    {
        var directory = Path.Combine(Root, "Patchers", "VisibilityProbe");
        var project = Path.Combine(directory, "VisibilityProbe");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(project, "VisibilityProbe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <NoWarn>CS0436</NoWarn>
                <CETCompat>false</CETCompat>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Mutagen.Bethesda.Synthesis" Version="0.36.6" />
                <PackageReference Include="Mutagen.Bethesda.Skyrim" Version="0.54.4" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Program.cs"), $$"""
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;

            public static class Program
            {
                public static Task<int> Main(string[] args) =>
                    SynthesisPipeline.Instance
                        .AddPatch<ISkyrimMod, ISkyrimModGetter>(Probe)
                        .SetTypicalOpen(GameRelease.SkyrimSE, "VisibilityProbe.esp")
                        .Run(args);

                private static void Probe(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    var fromLoadOrder = state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides()
                        .Single(l => l.EditorID == "{{ListEditorId}}");
                    var fromLinkCache = state.LinkCache.Resolve<ILeveledItemGetter>(fromLoadOrder.FormKey);
                    string Describe(ILeveledItemGetter l) => string.Join(",", l.Entries!.Select(e => e.Data!.Reference.FormKey.ToString()));
                    File.WriteAllLines(@"{{ProbeReport}}", ["loadorder=" + Describe(fromLoadOrder), "linkcache=" + Describe(fromLinkCache)]);
                }
            }
            """);
        var sln = Path.Combine(directory, "VisibilityProbe.sln");
        Processes.Run("dotnet", ["new", "sln", "-n", "VisibilityProbe", "--format", "sln"], directory);
        Processes.Run("dotnet", ["sln", sln, "add", Path.Combine(project, "VisibilityProbe.csproj")], directory);
        return sln;
    }

    /// <summary>The probe's report: key → list of FormKeys the following patcher saw.</summary>
    public IReadOnlyDictionary<string, string[]> ReadProbe() =>
        File.ReadAllLines(ProbeReport).Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1].Split(',', StringSplitOptions.RemoveEmptyEntries));

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SafeSynthesis.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        // Set SAFEPATCH_E2E_KEEP=1 to keep the workspace for debugging.
        if (Environment.GetEnvironmentVariable("SAFEPATCH_E2E_KEEP") == "1") return;
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a lingering process may still hold a file; temp cleanup is best effort */ }
        catch (UnauthorizedAccessException) { }
    }
}
