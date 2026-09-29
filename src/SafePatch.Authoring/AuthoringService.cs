using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Compiler;
using SafePatch.Generator;
using SafePatch.Host;
using SafePatch.Mutagen;
using SafePatch.Sandbox.Windows;
using SafePatch.Synthesis;

namespace SafePatch.Authoring;

public sealed record PluginInfo(int Index, string ModKey, IReadOnlyList<string> Masters, int Records);

public sealed record RecordSummary(string FormKey, string Type, string? EditorId, string WinningPlugin);

public sealed record RecordDetail(RecordSummary Record, string Text);

/// <param name="ChangedFields">Fields that differ from the version below it; empty for the original.</param>
public sealed record RecordVersion(string Plugin, IReadOnlyList<string> ChangedFields, string Text);

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
public sealed class AuthoringService(LoadOrderSnapshot snapshot, IWorkerLauncher? launcher = null)
{
    /// <summary>The SafePatch.Synthesis version packaged patchers reference: this runtime's own.</summary>
    public static string RuntimeVersion { get; } =
        typeof(SafePatchHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public IReadOnlyList<PluginInfo> GetLoadOrder() =>
        [.. snapshot.Mods.Select((mod, index) => new PluginInfo(
            index, mod.ModKey.FileName, [.. mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String)], mod.EnumerateMajorRecords().Count()))];

    /// <summary>The winning version of a record, by FormKey (<c>012E49:Skyrim.esm</c>) or EditorID.</summary>
    public RecordDetail GetRecord(string id)
    {
        var context = Resolve(id);
        return new RecordDetail(Summary(context.Record, context.ModKey), RecordText.Print(context.Record));
    }

    /// <summary>Every version of a record, the original first, with the fields each plugin changed.</summary>
    public IReadOnlyList<RecordVersion> GetOverrideChain(string id)
    {
        var winner = Resolve(id).Record;
        var chain = snapshot.LinkCache.ResolveAllContexts(winner.FormKey, winner.Registration.GetterType).Reverse().ToList();
        return [.. chain.Select((context, i) => new RecordVersion(
            context.ModKey.FileName,
            i == 0 ? [] : RecordDiff.ChangedFields(chain[i - 1].Record, context.Record),
            RecordText.Print(context.Record)))];
    }

    /// <summary>Winning records of a type (e.g. <c>LeveledItem</c>), optionally whose EditorID contains some text.</summary>
    public IReadOnlyList<RecordSummary> QueryRecords(string type, string? editorIdContains = null, int limit = 100) =>
        [.. Winners(GetterType(type))
            .Where(w => editorIdContains is null || (w.Record.EditorID?.Contains(editorIdContains, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(limit)
            .Select(w => Summary(w.Record, w.Plugin))];

    /// <summary>Winning records that link to a record.</summary>
    public IReadOnlyList<RecordSummary> FindReferences(string id, int limit = 100)
    {
        var target = Resolve(id).Record.FormKey;
        return [.. Winners(typeof(IMajorRecordGetter))
            .Where(w => w.Record.EnumerateFormLinks().Any(l => l.FormKey == target))
            .Take(limit)
            .Select(w => Summary(w.Record, w.Plugin))];
    }

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
    /// <param name="gameIni">The game INI listing archives; by default, Synthesis's lookup.</param>
    public TestResult TestPatch(string source, PatchScope scope, SettingsInput? settings = null, string? gameIni = null, CancellationToken cancel = default)
    {
        var compiled = PatchCompiler.Compile(source, settings?.Source);
        if (!compiled.Success) return new TestResult(false, "The program does not compile under the sandbox policy.", compiled.Diagnostics, "", [], []);

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
            var linkCache = snapshot.Mods.ToMutableLinkCache<ISkyrimMod, ISkyrimModGetter>(patchMod);
            var committer = new MutagenPatchCommitter<ISkyrimMod, ISkyrimModGetter>(patchMod, linkCache, snapshot.Release, format:
                SynthesisInputs.Format(SynthesisInputs.Parse(arguments), [.. snapshot.Mods.Select(m => m.ModKey), patchMod.ModKey]));
            var session = new PatchSession(launcher ?? Sandbox(), committer);
            try
            {
                var report = session.Run(package, SynthesisInputs.Plan(arguments, manifestSettings?.Path, gameIni), cancel);
                return new TestResult(true, null, [], report.Log, [.. report.Changes.Select(c => Detail(c, patchMod))], report.DeniedAssets ?? []);
            }
            catch (PatchRejectedException e)
            {
                return new TestResult(false, e.Message, [], e.Log ?? "", [], []);
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private static AppContainerLauncher Sandbox() => OperatingSystem.IsWindows()
        ? new AppContainerLauncher(new SandboxOptions { WorkerPath = SafePatchHost.WorkerPath })
        : throw new SafePatchException("Test runs need Windows for the worker sandbox.");

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
        snapshot.LinkCache.TryResolve(key, (after?.Registration ?? LoquiRegistrationFor(change.RecordType)).GetterType, out var before);
        var fields = change.IsNew || change.IsRemoved ? [] : change.Fields;
        return new ChangeDetail(change, [.. fields.Select(f => new FieldChange(f,
            before is null ? null : RecordText.Field(before, f),
            after is null ? null : RecordText.Field(after, f)))]);
    }

    private IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter> Resolve(string id)
    {
        if (FormKey.TryFactory(id, out var formKey) && snapshot.LinkCache.TryResolveContext(formKey, typeof(IMajorRecordGetter), out var byKey))
            return byKey;
        var byEditorId = Winners(typeof(IMajorRecordGetter)).FirstOrDefault(w => string.Equals(w.Record.EditorID, id, StringComparison.OrdinalIgnoreCase)).Record;
        if (byEditorId is not null && snapshot.LinkCache.TryResolveContext(byEditorId.FormKey, byEditorId.Registration.GetterType, out var context))
            return context;
        throw new SafePatchException($"No record {id} in the load order.");
    }

    /// <summary>Each record's winning version, highest priority plugin first.</summary>
    private IEnumerable<(IMajorRecordGetter Record, ModKey Plugin)> Winners(Type type)
    {
        var seen = new HashSet<FormKey>();
        foreach (var mod in snapshot.Mods.Reverse())
        {
            foreach (var record in mod.EnumerateMajorRecords(type))
                if (seen.Add(record.FormKey)) yield return (record, mod.ModKey);
        }
    }

    private static Type GetterType(string type) =>
        typeof(ISkyrimModGetter).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{type}Getter") is { } getter && typeof(IMajorRecordGetter).IsAssignableFrom(getter)
            ? getter
            : throw new SafePatchException($"{type} is not a Skyrim record type (use Mutagen's class name, e.g. LeveledItem).");

    private static Loqui.ILoquiRegistration LoquiRegistrationFor(string type) =>
        Loqui.LoquiRegistration.GetRegister(GetterType(type));

    private static RecordSummary Summary(IMajorRecordGetter record, ModKey winner) =>
        new(record.FormKey.ToString(), RecordDiff.TypeName(record), record.EditorID, winner.FileName);
}
