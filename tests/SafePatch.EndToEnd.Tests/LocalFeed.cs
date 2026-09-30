namespace SafePatch.EndToEnd.Tests;

/// <summary>
/// The SafePatch.Synthesis package, packed once per test run into a local folder feed, as a patcher
/// would get it from nuget.org. Each run's version is new, so no NuGet cache can serve a stale package.
/// </summary>
public static class LocalFeed
{
    private static readonly Lazy<(string Folder, string Version)> Packed = new(() =>
    {
        var version = $"0.0.0-e2e.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        foreach (var earlier in Directory.EnumerateDirectories(Path.GetTempPath(), "SafePatchFeed-*"))
        {
            try { Directory.Delete(earlier, recursive: true); } catch (IOException) { }
        }
        var folder = Path.Combine(Path.GetTempPath(), $"SafePatchFeed-{version}");
        // The repo is already built, so this build is incremental. PackageVersion leaves the assemblies alone.
        var result = Processes.Run("dotnet",
        [
            "pack", Path.Combine(Workspace.RepoRoot, "src", "SafePatch.Synthesis", "SafePatch.Synthesis.csproj"),
            "-o", folder, $"-p:PackageVersion={version}",
        ], Workspace.RepoRoot);
        Assert.True(result.ExitCode == 0, result.ToString());
        return (folder, version);
    });

    public static string Version => Packed.Value.Version;

    /// <summary>
    /// Writes a NuGet.config for a generated solution: SafePatch packages only from the local feed, Mutagen's from the
    /// fork's feed (as the repository's NuGet.config has it, until the fork's changes are released), everything else
    /// from nuget.org, into a packages folder kept apart from the user's.
    /// </summary>
    public static void Configure(string solutionDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "SafePatch", "e2e-packages");
        var mutagenFork = Path.Combine(localAppData, "SafePatch", "mutagen-fork");
        // Test versions are single-use; drop earlier ones so the folder does not grow.
        var old = Path.Combine(packages, "safepatch.synthesis");
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);

        File.WriteAllText(Path.Combine(solutionDirectory, "NuGet.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packages}" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="{Packed.Value.Folder}" />
                <add key="mutagen-fork" value="{mutagenFork}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local">
                  <package pattern="SafePatch.*" />
                </packageSource>
                <packageSource key="mutagen-fork">
                  <package pattern="Mutagen.Bethesda.Kernel" />
                  <package pattern="Mutagen.Bethesda.Core" />
                  <package pattern="Mutagen.Bethesda.Skyrim" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
    }
}
