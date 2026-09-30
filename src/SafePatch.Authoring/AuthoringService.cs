using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Query;
using SafePatch.Compiler;
using SafePatch.Generator;
using SafePatch.Host;
using SafePatch.Mutagen;
using SafePatch.Sandbox.Windows;
using SafePatch.Synthesis;

namespace SafePatch.Authoring;

/// <summary>The authority a program asks for, as its manifest will state it.</summary>
public sealed record PatchScope(
    IReadOnlyList<string> Writable,
    IReadOnlyList<string>? Creatable = null,
    IReadOnlyList<string>? Removable = null,
    IReadOnlyList<string>? Assets = null,
    int MaxRecords = 10_000);

/// <summary>Optional settings: the data-only settings source, and the values to run with (JSON).</summary>
public sealed record SettingsInput(string Source, string? Json = null);

/// <summary>Whether a program compiles under the sandbox policy; the compiled bytes stay out of reports.</summary>
public sealed record ValidationResult(bool Success, IReadOnlyList<PolicyDiagnostic> Diagnostics, string? ProgramSha256, string? SettingsType);

public sealed record FieldChange(string Field, string? Before, string? After);

public sealed record ChangeDetail(RecordChange Change, IReadOnlyList<FieldChange> Fields);

/// <param name="Diagnostics">Compiler and policy diagnostics when the program did not compile.</param>
public sealed record TestResult(
    bool Accepted, string? Error, IReadOnlyList<PolicyDiagnostic> Diagnostics, string Log, IReadOnlyList<ChangeDetail> Changes, IReadOnlyList<string> DeniedAssets);

public sealed record PackageRequest(
    string Name, string Source, PatchScope Scope, string OutputDirectory,
    SettingsInput? Settings = null, string? Description = null, IReadOnlyList<string>? RequiredMods = null,
    string? RuntimeProject = null);

/// <summary>
/// What an agent (through the CLI or MCP) can do: read the load order, check and test a program in
/// the real sandbox against it, and package it as a Synthesis patcher. Reads the load order only;
/// test runs write to a temporary folder, and packaging only to the directory it is given.
/// </summary>
/// <param name="workerMemoryBytes">The worker's memory limit, when not the sandbox's default (<see cref="SandboxOptions.MemoryLimitBytes"/>).</param>
public sealed class AuthoringService(LoadOrderSnapshot snapshot, IWorkerLauncher? launcher = null, ulong? workerMemoryBytes = null)
{
    /// <summary>The SafePatch.Synthesis version packaged patchers reference: this runtime's own.</summary>
    private const string RunningPatch = "Running patch.";
    private const string FinishedPatch = "Finished patch.";

    public static string RuntimeVersion { get; } =
        typeof(SafePatchHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Compiles a program under the sandbox's API policy. Needs no load order.</summary>
    public static ValidationResult Validate(string source, string? settingsSource = null)
    {
        var result = PatchCompiler.Compile(source, settingsSource);
        return new ValidationResult(result.Success, result.Diagnostics, result.Sha256, result.SettingsType);
    }

    /// <summary>
    /// Runs a program in the sandbox against the load order, as Synthesis would, and reports what the
    /// host would accept, field by field. The patch goes to a temporary folder and is discarded.
    /// </summary>
    /// <param name="gameIni">The game INI listing archives; by default, the load order source's (Synthesis's lookup for a plain Data folder).</param>
    public TestResult TestPatch(string source, PatchScope scope, SettingsInput? settings = null, string? gameIni = null, CancellationToken cancel = default)
    {
        var compiled = PatchCompiler.Compile(source, settings?.Source);
        if (!compiled.Success) return new TestResult(false, "The program does not compile under the sandbox policy.", compiled.Diagnostics, "", [], []);

        return RunProgram(compiled, scope, settings, gameIni, commit: true,
            (report, patchMod) => new TestResult(true, null, [], report.Log, [.. report.Changes.Select(c => Detail(c, patchMod))], report.DeniedAssets ?? []),
            rejected => new TestResult(false, rejected.Message, [], rejected.Log ?? "", [], []),
            cancel);
    }

    /// <summary>
    /// Runs a read-only query program in the sandbox: a <c>RunPatch</c> function that prints its answer with
    /// <c>Console.WriteLine</c>. Whatever it does to the patch is discarded unread; its output is the result, one
    /// line per printed line. It is compiled under the same policy as a patch and never runs in this process.
    /// </summary>
    /// <param name="assets">Loose Data files it may read, as globs; none by default. Archives are always readable.</param>
    public Output.ResultSet RunQuery(string source, IReadOnlyList<string>? assets = null, string? gameIni = null, CancellationToken cancel = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var compiled = PatchCompiler.Compile(source);
        if (!compiled.Success)
        {
            return Output.ResultSet.Lines("Query did not compile under the sandbox policy",
                compiled.Diagnostics.Select(d => $"{d.Code} line {d.Line}: {d.Message}")) with { Generation = snapshot.Generation, Failed = true };
        }

        var set = RunProgram(compiled, new PatchScope([], Assets: assets, MaxRecords: 1), null, gameIni, commit: false,
            (report, _) => Output.ResultSet.Lines("Query output", Lines(report.Log)),
            rejected => Output.ResultSet.Lines("Query failed", Lines(rejected.Log ?? ""), notes: [$"The query failed: {rejected.Message}", "Its output up to the failure follows."]) with { Failed = true },
            cancel);
        return set with { Elapsed = watch.Elapsed, Generation = snapshot.Generation };

        static IEnumerable<string> Lines(string log) => ProgramOutput(log.ReplaceLineEndings("\n").Split('\n')).Where(l => l.Length > 0);
    }

    /// <summary>
    /// What the program printed, without the pipeline's own log around it: Synthesis (the pinned version) logs
    /// <c>Running patch.</c> before calling the program and <c>Finished patch.</c> after it returns. A program that
    /// threw has no end line; if Synthesis never started it, the whole log is kept.
    /// </summary>
    internal static IEnumerable<string> ProgramOutput(IReadOnlyList<string> log)
    {
        var start = log.ToList().IndexOf(RunningPatch);
        if (start < 0) return log;
        var end = log.ToList().LastIndexOf(FinishedPatch);
        return log.Skip(start + 1).Take((end > start ? end : log.Count) - start - 1);
    }

    /// <summary>
    /// Runs a compiled program in the sandbox against the snapshot, as Synthesis would: its output goes to a temporary
    /// folder that is deleted. With <paramref name="commit"/>, the host validates and applies the output to a patch mod
    /// that <paramref name="accepted"/> can inspect; otherwise the output is discarded unread.
    /// </summary>
    private T RunProgram<T>(CompileResult compiled, PatchScope scope, SettingsInput? settings, string? gameIni, bool commit,
        Func<SessionReport, SkyrimMod, T> accepted, Func<PatchRejectedException, T> rejected, CancellationToken cancel)
    {
        var work = Directory.CreateTempSubdirectory("SafePatchTest-");
        try
        {
            var manifestSettings = compiled.SettingsType is { } settingsType ? new ManifestSettings(settingsType, PatcherGenerator.SettingsFile) : null;
            var extraData = Path.Combine(work.FullName, "ExtraData");
            Directory.CreateDirectory(extraData);
            if (settings?.Json is { } json) File.WriteAllText(Path.Combine(extraData, PatcherGenerator.SettingsFile), json);

            var output = Path.Combine(work.FullName, "Output", "Synthesis.esp");
            string[] arguments =
            [
                "run-patcher", "--GameRelease", snapshot.Release.ToString(), "--DataFolderPath", snapshot.DataFolder,
                "--LoadOrderFilePath", snapshot.PluginsFile, "--OutputPath", output, "--ModKey", Path.GetFileName(output),
                "--ExtraDataFolder", extraData, "--SplitIfMaxMastersExceeded",
            ];
            var manifest = new Manifest(Manifest.CurrentSchemaVersion, "AuthoringTest", compiled.Sha256!, scope.Writable, scope.Creatable ?? [], scope.MaxRecords,
                manifestSettings, scope.Assets, scope.Removable);
            var package = Preflight.Verify(manifest.ToJson(), compiled.Assembly!, PatchPolicy.PublisherDefault);

            var patchMod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(output)), snapshot.Release.ToSkyrimRelease());
            IPatchCommitter committer = commit
                ? new MutagenPatchCommitter<ISkyrimMod, ISkyrimModGetter>(patchMod, snapshot.Mods.ToMutableLinkCache<ISkyrimMod, ISkyrimModGetter>(patchMod), snapshot.Release,
                    format: SynthesisInputs.Format(SynthesisInputs.Parse(arguments), [.. snapshot.Mods.Select(m => m.ModKey), patchMod.ModKey]))
                : new DiscardingCommitter();
            // The worker sees the snapshot's Data folder, layered as a mod manager would, whether or not one runs.
            var assets = new LayeredAssetSource([.. snapshot.View.Layers.Reverse().Select(l => new DataFolderAssetSource(l.Root))]);
            var session = new PatchSession(launcher ?? Sandbox(workerMemoryBytes), committer, _ => assets);
            try
            {
                var inputs = SynthesisInputs.Plan(arguments, manifestSettings?.Path, gameIni ?? snapshot.GameIni, snapshot.View.InputFiles());
                return accepted(session.Run(package, inputs, cancel), patchMod);
            }
            catch (PatchRejectedException e)
            {
                return rejected(e);
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A query's output plugin is never read, so nothing it did to the patch can matter.</summary>
    private string? EditorIdOf(FormKey formKey) => snapshot.Index.Find(formKey)?.EditorId;

    private sealed class DiscardingCommitter : IPatchCommitter
    {
        public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy) => [];
    }

    private static AppContainerLauncher Sandbox(ulong? memoryBytes) => OperatingSystem.IsWindows()
        ? new AppContainerLauncher(memoryBytes is { } limit
            ? new SandboxOptions { WorkerPath = SafePatchHost.WorkerPath, MemoryLimitBytes = limit }
            : new SandboxOptions { WorkerPath = SafePatchHost.WorkerPath })
        : throw new SafePatchException("Test runs need Windows for the worker sandbox.");

    /// <summary>A front end's <c>--worker-memory</c> option (MiB) in bytes, or null to keep the sandbox's default.</summary>
    public static ulong? WorkerMemory(string? mebibytes) =>
        mebibytes is null ? null : SandboxOptions.ParseMemoryLimit(mebibytes, "--worker-memory");

    /// <summary>Writes a Synthesis patcher repository for the program (see <see cref="PatcherGenerator"/>). Needs no load order.</summary>
    public static GenerateResult Package(PackageRequest request) => PatcherGenerator.Generate(new PatcherSpec
    {
        Name = request.Name,
        OutputPlugin = $"{request.Name}.esp",
        Source = request.Source,
        SettingsSource = request.Settings?.Source,
        Writable = request.Scope.Writable,
        Creatable = request.Scope.Creatable ?? [],
        Removable = request.Scope.Removable ?? [],
        Assets = request.Scope.Assets ?? [],
        MaxRecords = request.Scope.MaxRecords,
        Description = request.Description,
        RequiredMods = request.RequiredMods ?? [],
        Runtime = request.RuntimeProject is { } project ? new RuntimeReference.Project(project) : new RuntimeReference.Package(RuntimeVersion),
    }, request.OutputDirectory);

    /// <summary>Before (the load order's winner) and after (the patch) for each changed field.</summary>
    private ChangeDetail Detail(RecordChange change, ISkyrimModGetter patchMod)
    {
        var key = FormKey.Factory(change.FormKey);
        var after = change.IsRemoved ? null : patchMod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key);
        snapshot.LinkCache.TryResolve(key, (after?.Registration ?? RecordTypes.Registration(change.RecordType)).GetterType, out var before);
        var fields = change.IsNew || change.IsRemoved ? [] : change.Fields;
        return new ChangeDetail(change, [.. fields.Select(f => new FieldChange(f,
            before is null ? null : RecordText.Field(before, f, EditorIdOf),
            after is null ? null : RecordText.Field(after, f, EditorIdOf)))]);
    }

}
