using System.Xml.Linq;

namespace SafePatch.Architecture.Tests;

/// <summary>
/// Enforces the dependency direction: the host core is pure, the worker cannot reach host
/// authority, and only edge projects touch Mutagen, Synthesis, Roslyn or Win32.
/// </summary>
public class DependencyRulesTests
{
    /// <summary>Every src project and exactly the project/package references it may have.</summary>
    private static readonly Dictionary<string, (string[] Projects, string[] Packages)> Allowed = new()
    {
        ["SafePatch.Protocol"] = ([], []),
        ["SafePatch.Host"] = (["SafePatch.Protocol"], []),
        // The worker runs real Mutagen/Synthesis, but only over its brokered, in-memory file system.
        ["SafePatch.Worker.Core"] = (["SafePatch.Protocol"],
            ["Mutagen.Bethesda.Synthesis", "Mutagen.Bethesda.Skyrim", "TestableIO.System.IO.Abstractions.TestingHelpers"]),
        ["SafePatch.Worker"] = (["SafePatch.Worker.Core"], []),
        ["SafePatch.Mutagen"] = (["SafePatch.Host"], ["Mutagen.Bethesda.Skyrim"]),
        ["SafePatch.Sandbox.Windows"] = (["SafePatch.Host"], []),
        // The package carries SafePatch.Mutagen inside it, so it declares Mutagen.Bethesda.Skyrim itself.
        ["SafePatch.Synthesis"] = (["SafePatch.Host", "SafePatch.Mutagen", "SafePatch.Sandbox.Windows", "SafePatch.Worker"], ["Mutagen.Bethesda.Synthesis", "Mutagen.Bethesda.Skyrim"]),
        ["SafePatch.Compiler"] = ([], ["Microsoft.CodeAnalysis.CSharp", "Basic.Reference.Assemblies.Net100", "Mutagen.Bethesda.Synthesis", "Mutagen.Bethesda.Skyrim"]),
        ["SafePatch.Generator"] = (["SafePatch.Compiler", "SafePatch.Host"], []),
        // The authoring service composes the rest for the CLI and MCP front ends; it reads load orders, never writes them.
        ["SafePatch.Authoring"] = (["SafePatch.Compiler", "SafePatch.Generator", "SafePatch.Host", "SafePatch.Mutagen", "SafePatch.Synthesis", "SafePatch.Sandbox.Windows"],
            ["Mutagen.Bethesda.Skyrim"]),
        ["SafePatch.Cli"] = (["SafePatch.Authoring"], []),
        ["SafePatch.Mcp"] = (["SafePatch.Authoring"], ["ModelContextProtocol", "Microsoft.Extensions.Hosting"]),
    };

    public static TheoryData<string> SourceProjects() => new(ProjectFiles().Select(Path.GetFileNameWithoutExtension)!);

    [Fact]
    public void Every_src_project_has_a_rule() =>
        Assert.All(ProjectFiles().Select(Path.GetFileNameWithoutExtension), p => Assert.Contains(p!, Allowed.Keys));

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_only_what_its_rule_allows(string project)
    {
        var xml = XDocument.Load(ProjectFiles().Single(f => Path.GetFileNameWithoutExtension(f) == project));
        var projects = xml.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')));
        var packages = xml.Descendants("PackageReference").Select(e => e.Attribute("Include")!.Value);

        var (allowedProjects, allowedPackages) = Allowed[project];
        Assert.Empty(projects.Except(allowedProjects));
        Assert.Empty(packages.Except(allowedPackages));
    }

    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SafeSynthesis.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
