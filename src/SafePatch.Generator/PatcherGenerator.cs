using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SafePatch.Compiler;
using SafePatch.Host;

namespace SafePatch.Generator;

/// <summary>How the generated patcher obtains the trusted SafePatch runtime.</summary>
public abstract record RuntimeReference
{
    /// <summary>A pinned, publisher-reviewed NuGet package (for releases).</summary>
    public sealed record Package(string Version) : RuntimeReference;

    /// <summary>A local SafePatch.Synthesis project (for development and local-solution patchers).</summary>
    public sealed record Project(string CsprojPath) : RuntimeReference;
}

public sealed record PatcherSpec
{
    /// <summary>Repository, solution and project name, e.g. <c>CacoLeveledLists</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Default plugin name when run standalone; Synthesis supplies the real one.</summary>
    public required string OutputPlugin { get; init; }

    /// <summary>The agent-authored program: a class with <c>public static void RunPatch(IPatcherState&lt;ISkyrimMod, ISkyrimModGetter&gt;)</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Fields overrides may change, e.g. <c>LeveledItem.Entries</c> (see <see cref="Manifest.Writable"/>).</summary>
    public required IReadOnlyList<string> Writable { get; init; }

    /// <summary>
    /// Optional data-only settings classes shown in Synthesis's settings GUI. Checked by
    /// <see cref="SettingsPolicyChecker"/>, then compiled into both the program and the patcher.
    /// </summary>
    public string? SettingsSource { get; init; }

    /// <summary>Record types the patch may create.</summary>
    public IReadOnlyList<string> Creatable { get; init; } = [];

    /// <summary>Record types the patch may remove from the patch mod (see <see cref="Manifest.Removable"/>).</summary>
    public IReadOnlyList<string> Removable { get; init; } = [];

    /// <summary>One line shown under the patcher's name in Synthesis.</summary>
    public string? Description { get; init; }

    /// <summary>Plugins the patcher needs in the load order (e.g. <c>Complete Alchemy &amp; Cooking Overhaul.esp</c>); Synthesis warns when one is missing.</summary>
    public IReadOnlyList<string> RequiredMods { get; init; } = [];

    /// <summary>Loose Data files the program may read, as Data-relative globs (see <see cref="Manifest.Assets"/>).</summary>
    public IReadOnlyList<string> Assets { get; init; } = [];

    public int MaxRecords { get; init; } = 10_000;
    public required RuntimeReference Runtime { get; init; }
}

public sealed record GenerateResult(bool Success, IReadOnlyList<PolicyDiagnostic> Diagnostics, string? ProgramSha256);

/// <summary>
/// Compiles the program under policy and writes a patcher repository whose only trusted code is
/// the fixed template. Agent input only ever lands in <c>payload/</c> as inert content.
/// </summary>
public static partial class PatcherGenerator
{
    /// <summary>The settings file under the patcher's extra data folder, as Synthesis's GUI saves it.</summary>
    public const string SettingsFile = "settings.json";

    public static GenerateResult Generate(PatcherSpec spec, string outputDirectory)
    {
        if (!NamePattern().IsMatch(spec.Name)) throw new ArgumentException("Name must be a simple identifier.", nameof(spec));
        if (!PluginPattern().IsMatch(spec.OutputPlugin)) throw new ArgumentException("Output plugin must be a plain .esp file name.", nameof(spec));
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException($"{outputDirectory} is not empty.");

        var compiled = PatchCompiler.Compile(spec.Source, spec.SettingsSource);
        if (!compiled.Success) return new GenerateResult(false, compiled.Diagnostics, null);

        var settings = compiled.SettingsType is { } settingsType ? new ManifestSettings(settingsType, SettingsFile) : null;
        var manifest = new Manifest(Manifest.CurrentSchemaVersion, spec.Name, compiled.Sha256!, spec.Writable, spec.Creatable, spec.MaxRecords, settings, spec.Assets, spec.Removable);
        // Fail now, not at run time, if the manifest asks for more than the publisher allows.
        Preflight.Verify(manifest.ToJson(), compiled.Assembly!, PatchPolicy.PublisherDefault);

        var project = Path.Combine(outputDirectory, spec.Name);
        var payload = Path.Combine(project, "payload");
        Directory.CreateDirectory(payload);

        var values = new Dictionary<string, string>
        {
            ["Name"] = spec.Name,
            ["OutputPlugin"] = spec.OutputPlugin,
            ["ProjectGuid"] = DeterministicGuid(spec.Name).ToString("D").ToUpperInvariant(),
            ["RuntimeReference"] = spec.Runtime switch
            {
                // Exactly this version: the payload's manifest schema is the one this runtime reads (ADR 007).
                RuntimeReference.Package p => $"<PackageReference Include=\"SafePatch.Synthesis\" Version=\"[{SecurityElement.Escape(p.Version)}]\" />",
                RuntimeReference.Project p => $"<ProjectReference Include=\"{SecurityElement.Escape(Path.GetFullPath(p.CsprojPath))}\" />",
                _ => throw new ArgumentException("Unknown runtime reference.", nameof(spec)),
            },
            // The manifest check above guarantees the type is a plain dotted identifier.
            ["SettingsRegistration"] = settings is null ? "" :
                $"{Environment.NewLine}            .SetAutogeneratedSettings<global::{settings.Type}>(\"{spec.Name}\", \"{settings.Path}\", out _)",
            ["SettingsCompile"] = settings is null ? "" : $"{Environment.NewLine}    <Compile Include=\"Settings.cs\" />",
        };

        WriteTemplate("Solution.sln", Path.Combine(outputDirectory, $"{spec.Name}.sln"), values);
        WriteTemplate("Directory.Build.props", Path.Combine(outputDirectory, "Directory.Build.props"), values);
        WriteTemplate("Directory.Build.targets", Path.Combine(outputDirectory, "Directory.Build.targets"), values);
        WriteTemplate("Patcher.csproj", Path.Combine(project, $"{spec.Name}.csproj"), values);
        WriteTemplate("Program.cs", Path.Combine(project, "Program.cs"), values);
        File.WriteAllText(Path.Combine(project, SynthesisMetaFile), SynthesisMeta(spec));

        // Checked by the settings policy above: data-only classes that Synthesis's settings GUI reflects over.
        if (settings is not null) File.WriteAllText(Path.Combine(project, "Settings.cs"), spec.SettingsSource);
        File.WriteAllBytes(Path.Combine(payload, "patch.bin"), compiled.Assembly!);
        File.WriteAllText(Path.Combine(payload, "patch.safe.cs"), spec.Source);
        File.WriteAllText(Path.Combine(payload, "manifest.json"), manifest.ToJson());
        File.WriteAllText(Path.Combine(outputDirectory, "REVIEW.md"), ReviewNotes(spec, manifest));

        return new GenerateResult(true, [], compiled.Sha256);
    }

    /// <summary>What Synthesis reads next to a patcher's project to name and describe it.</summary>
    public const string SynthesisMetaFile = "SynthesisMeta.json";

    /// <summary>The Skyrim releases whose plugins the sandboxed pipeline reads (the SE format).</summary>
    public static readonly IReadOnlyList<string> TargetedReleases = ["SkyrimSE", "SkyrimSEGog", "SkyrimVR"];

    private static string SynthesisMeta(PatcherSpec spec) => JsonSerializer.Serialize(
        new
        {
            Nickname = spec.Name,
            Visibility = "Visible",
            OneLineDescription = spec.Description ?? "A sandboxed SafePatch patcher.",
            LongDescription = "Generated by SafePatch. Its program runs in a sandbox, and only the changes its manifest allows are applied. See REVIEW.md.",
            PreferredAutoVersioning = "Default",
            RequiredMods = spec.RequiredMods,
            TargetedReleases,
        },
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static void WriteTemplate(string template, string path, IReadOnlyDictionary<string, string> values)
    {
        using var stream = typeof(PatcherGenerator).Assembly.GetManifestResourceStream($"Template.{template}.template")
                           ?? throw new InvalidOperationException($"Missing template {template}.");
        var text = new StreamReader(stream).ReadToEnd();
        foreach (var (key, value) in values) text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        File.WriteAllText(path, text);
    }

    private static string ReviewNotes(PatcherSpec spec, Manifest manifest)
    {
        var notes = new StringBuilder();
        notes.AppendLine($"# {spec.Name}").AppendLine();
        notes.AppendLine("Generated by SafePatch.Generator. Only `payload/` came from the agent; every other file is the trusted template.").AppendLine();
        notes.AppendLine($"- Program SHA-256: `{manifest.ProgramSha256}`");
        notes.AppendLine($"- Writable fields: {Join(manifest.Writable)}");
        notes.AppendLine($"- Creatable record types: {Join(manifest.Creatable)}");
        notes.AppendLine($"- Removable record types: {Join(manifest.Removable ?? [])}");
        notes.AppendLine($"- Max records: {manifest.MaxRecords}");
        notes.AppendLine($"- Readable assets: {Join(manifest.Assets ?? [])}");
        if (manifest.Settings is { } settings)
            notes.AppendLine($"- Settings: `{settings.Type}`, from `{settings.Path}`. `{spec.Name}/Settings.cs` is compiled into the patcher: review it too.");
        notes.AppendLine().AppendLine("Review `payload/patch.safe.cs` before publishing.");
        return notes.ToString();
    }

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items.Select(i => $"`{i}`"));

    private static Guid DeterministicGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("SafePatch:" + name)));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,59}\\.esp$")]
    private static partial Regex PluginPattern();
}
