using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SafePatch.Host;

namespace SafePatch.TestSupport;

/// <summary>
/// Compiles test programs WITHOUT the safety policy (so tests can build hostile programs) against
/// everything the test process can load, and wraps them in verified packages.
/// </summary>
public static class TestPrograms
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray());

    public static byte[] Compile(string source, string assemblyName = "TestProgram", string? settingsSource = null)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            settingsSource is null ? [CSharpSyntaxTree.ParseText(source)] : [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(settingsSource)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    public static VerifiedPackage Package(
        byte[] program,
        IReadOnlyList<string>? writable = null,
        IReadOnlyList<string>? creatable = null,
        PatchPolicy? policy = null,
        ManifestSettings? settings = null,
        IReadOnlyList<string>? assets = null,
        IReadOnlyList<string>? removable = null,
        int maxRecords = 1000)
    {
        var manifest = new Manifest(
            Manifest.CurrentSchemaVersion,
            "TestPackage",
            Convert.ToHexStringLower(SHA256.HashData(program)),
            writable ?? SamplePrograms.LeveledListMergeWritable,
            creatable ?? [],
            MaxRecords: maxRecords,
            settings,
            assets,
            removable);
        return Preflight.Verify(manifest.ToJson(), program, policy ?? PatchPolicy.PublisherDefault);
    }
}

/// <summary>A committer that records what it was given and accepts it.</summary>
public sealed class RecordingCommitter : IPatchCommitter
{
    public IReadOnlyList<byte[]>? OutputPlugins { get; private set; }
    public byte[]? OutputPlugin => OutputPlugins?[0];
    public byte[]? Persistence { get; private set; }

    public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy)
    {
        OutputPlugins = outputPlugins;
        Persistence = persistence;
        return [];
    }
}
