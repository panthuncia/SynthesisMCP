using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SafePatch.Compiler;

/// <param name="SettingsType">Full name of the program's settings class, when it has settings.</param>
public sealed record CompileResult(byte[]? Assembly, IReadOnlyList<PolicyDiagnostic> Diagnostics, string? SettingsType = null)
{
    public bool Success => Assembly is not null;

    /// <summary>Lowercase hex SHA-256 of the assembly, as recorded in the manifest.</summary>
    public string? Sha256 => Assembly is null ? null : Convert.ToHexStringLower(SHA256.HashData(Assembly));
}

/// <summary>
/// Compiles one agent source file (and optionally its settings source) against the pinned framework reference pack and the Mutagen and
/// Synthesis assemblies the worker ships, with a fixed language version, no unsafe code and
/// deterministic output; then checks the entry point, applies <see cref="PolicyChecker"/> to the
/// program and <see cref="SettingsPolicyChecker"/> to the settings, and inspects the emitted metadata. Run it in an isolated build environment: Roslyn is not sandboxed.
/// </summary>
public static class PatchCompiler
{
    public const string AssemblyName = "SafePatch.Program";
    public const string EntryPointName = "RunPatch";
    public const int MaxSourceLength = 1024 * 1024;

    /// <summary>The patcher-state type <c>RunPatch</c> must take.</summary>
    private static readonly string StateTypeName =
        typeof(IPatcherState<ISkyrimMod, ISkyrimModGetter>).GetGenericTypeDefinition().FullName!;

    /// <summary>Mutagen, Synthesis and their non-framework dependencies, from the assemblies loaded here.</summary>
    private static readonly Lazy<IReadOnlyList<string>> MutagenClosure = new(() =>
    {
        var directory = Path.GetDirectoryName(typeof(IPatcherState<,>).Assembly.Location)!;
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>([typeof(IPatcherState<,>).Assembly.GetName().Name!, typeof(ISkyrimMod).Assembly.GetName().Name!]);
        while (pending.TryPop(out var name))
        {
            var path = Path.Combine(directory, name + ".dll");
            if (found.ContainsKey(name) || !File.Exists(path)) continue; // framework assemblies are not beside us
            found[name] = path;
            using var pe = new PEReader(File.OpenRead(path));
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
                pending.Push(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        }
        return [.. found.Values];
    });

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        [.. Net100.References.All, .. MutagenClosure.Value.Select(p => MetadataReference.CreateFromFile(p))]);

    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14, DocumentationMode.None, SourceCodeKind.Regular, preprocessorSymbols: []);

    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: false,
        deterministic: true,
        nullableContextOptions: NullableContextOptions.Enable,
        checkOverflow: true);

    /// <param name="settingsSource">
    /// Data-only settings classes, compiled with the program and also into the trusted patcher. The
    /// program receives them the native way: one <c>public static Lazy&lt;TSettings&gt;</c> field.
    /// </param>
    public static CompileResult Compile(string source, string? settingsSource = null)
    {
        if (source.Length > MaxSourceLength || settingsSource?.Length > MaxSourceLength) return Fail("SP0100", "source is too large");

        var program = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "patch.safe.cs");
        var settings = settingsSource is null ? null : CSharpSyntaxTree.ParseText(settingsSource, ParseOptions, path: "settings.safe.cs");
        var compilation = CSharpCompilation.Create(
            AssemblyName,
            settings is null ? [program] : [program, settings],
            References.Value,
            CompilationOptions);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var p = d.Location.GetLineSpan().StartLinePosition;
                return new PolicyDiagnostic(d.Id, d.GetMessage(), p.Line + 1, p.Character + 1);
            })
            .ToList();
        if (errors.Count > 0) return new CompileResult(null, errors);

        if (CheckEntryPoint(compilation) is { } entryError) return new CompileResult(null, [entryError]);

        var violations = PolicyChecker.Check(compilation, [program]).ToList();
        string? settingsType = null;
        if (settings is not null)
        {
            violations.AddRange(SettingsPolicyChecker.Check(compilation, settings));
            var field = FindSettingsField(compilation, settings);
            if (field is null)
                violations.Add(new("SP0310", "a program with settings must declare exactly one public static Lazy<TSettings> field, where TSettings is a settings class", 0, 0));
            else settingsType = field.Type is INamedTypeSymbol { TypeArguments: [var t] } ? t.ToDisplayString() : null;
        }
        if (violations.Count > 0) return new CompileResult(null, violations);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success) return Fail("SP0101", string.Join("; ", emit.Diagnostics.Select(d => d.GetMessage())));

        var bytes = stream.ToArray();
        var metadataProblems = InspectMetadata(bytes);
        return metadataProblems.Count > 0 ? new CompileResult(null, metadataProblems) : new CompileResult(bytes, [], settingsType);
    }

    /// <summary>The single public static, non-readonly <c>Lazy&lt;T&gt;</c> field whose T is declared in the settings source.</summary>
    private static IFieldSymbol? FindSettingsField(CSharpCompilation compilation, SyntaxTree settings)
    {
        var fields = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Member)
            .OfType<IFieldSymbol>()
            .Where(f => f is { IsStatic: true, IsReadOnly: false, DeclaredAccessibility: Accessibility.Public, ContainingType.DeclaredAccessibility: Accessibility.Public }
                        && f.Type is INamedTypeSymbol { IsGenericType: true } lazy
                        && lazy.ConstructedFrom.ToDisplayString() == "System.Lazy<T>"
                        && lazy.TypeArguments[0].DeclaringSyntaxReferences.Any(r => r.SyntaxTree == settings))
            .ToList();
        return fields.Count == 1 ? fields[0] : null;
    }

    /// <summary>Exactly one public static <c>RunPatch(IPatcherState&lt;ISkyrimMod, ISkyrimModGetter&gt;)</c> returning void or Task.</summary>
    private static PolicyDiagnostic? CheckEntryPoint(CSharpCompilation compilation)
    {
        var entryPoints = compilation.GetSymbolsWithName(EntryPointName, SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .Where(m => m is { IsStatic: true, DeclaredAccessibility: Accessibility.Public, ContainingType.DeclaredAccessibility: Accessibility.Public }
                        && m.Parameters is [{ Type: INamedTypeSymbol { IsGenericType: true } p }]
                        && $"{p.ConstructedFrom.ContainingNamespace}.{p.ConstructedFrom.MetadataName}" == StateTypeName
                        && p.TypeArguments.Select(t => t.ToDisplayString()).SequenceEqual(["Mutagen.Bethesda.Skyrim.ISkyrimMod", "Mutagen.Bethesda.Skyrim.ISkyrimModGetter"])
                        && (m.ReturnsVoid || m.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task"))
            .ToList();
        return entryPoints.Count == 1
            ? null
            : new PolicyDiagnostic("SP0200",
                $"the program must declare exactly one public static void or Task {EntryPointName}(IPatcherState<ISkyrimMod, ISkyrimModGetter> state); found {entryPoints.Count}", 0, 0);
    }

    /// <summary>
    /// Assemblies the compiler itself may reference on the program's behalf, and the only types it may
    /// use from them. Collection expressions that build a <c>List&lt;T&gt;</c> call <c>CollectionsMarshal</c>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> CompilerHelperAssemblies = new(StringComparer.Ordinal)
    {
        ["System.Runtime.InteropServices"] = new(StringComparer.Ordinal) { "System.Runtime.InteropServices.CollectionsMarshal" },
    };

    /// <summary>Checks the emitted assembly itself, independent of the source walk.</summary>
    private static List<PolicyDiagnostic> InspectMetadata(byte[] assembly)
    {
        var allowed = MutagenClosure.Value.Select(Path.GetFileNameWithoutExtension)
            // System.Drawing.Primitives holds Mutagen's colours; the type allow-list admits only Color and KnownColor of it.
            .Concat(["System.Runtime", "System.Collections", "System.Linq", "System.Memory", "System.Console", "System.Drawing.Primitives", "netstandard"])
            .Concat(CompilerHelperAssemblies.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<PolicyDiagnostic>();
        using var pe = new PEReader(ImmutableArray.Create(assembly));
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (!allowed.Contains(name)) problems.Add(new("SP0102", $"references assembly {name}", 0, 0));
        }
        foreach (var handle in reader.TypeReferences)
        {
            var type = reader.GetTypeReference(handle);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            var scope = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            var fullName = $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
            if (CompilerHelperAssemblies.TryGetValue(scope, out var types) && !types.Contains(fullName))
                problems.Add(new("SP0105", $"references {fullName} in {scope}", 0, 0));
        }
        if (reader.GetTableRowCount(TableIndex.ImplMap) > 0) problems.Add(new("SP0103", "assembly contains P/Invoke declarations", 0, 0));
        if (reader.GetTableRowCount(TableIndex.ModuleRef) > 0) problems.Add(new("SP0104", "assembly references native modules", 0, 0));
        return problems;
    }

    private static CompileResult Fail(string code, string message) => new(null, [new PolicyDiagnostic(code, message, 0, 0)]);
}
