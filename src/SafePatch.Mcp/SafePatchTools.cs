using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SafePatch.Authoring;
using SafePatch.Generator;
using SafePatch.Host;

namespace SafePatch.Mcp;

/// <summary>The load order the server was started with, if any. Validation and packaging need none.</summary>
public sealed class Workspace(LoadOrderSnapshot? snapshot) : IDisposable
{
    public LoadOrderSnapshot? Snapshot => snapshot;

    public AuthoringService Service => snapshot is null
        ? throw new McpException("This server was started without a load order: pass --data <Data folder> --plugins <plugins.txt>.")
        : new AuthoringService(snapshot);

    public void Dispose() => snapshot?.Dispose();
}

/// <summary>
/// The authoring operations as MCP tools, one to one. Programs are native Synthesis patch functions
/// (<c>public static void RunPatch(IPatcherState&lt;ISkyrimMod, ISkyrimModGetter&gt; state)</c>); record types and
/// fields use Mutagen's class and property names, e.g. <c>LeveledItem.Entries</c>.
/// </summary>
[McpServerToolType]
public sealed class SafePatchTools(Workspace workspace)
{
    [McpServerTool(Name = "get_load_order", ReadOnly = true), Description("The plugins in the load order, lowest priority first, with their masters and record counts.")]
    public IReadOnlyList<PluginInfo> GetLoadOrder() => Guard(() => workspace.Service.GetLoadOrder());

    [McpServerTool(Name = "get_record", ReadOnly = true), Description("The winning version of a record, printed in full.")]
    public RecordDetail GetRecord([Description("A FormKey such as 012E49:Skyrim.esm, or an EditorID.")] string id) =>
        Guard(() => workspace.Service.GetRecord(id));

    [McpServerTool(Name = "get_override_chain", ReadOnly = true), Description("Every version of a record, the original first, with the fields each plugin changed.")]
    public IReadOnlyList<RecordVersion> GetOverrideChain([Description("A FormKey or an EditorID.")] string id) =>
        Guard(() => workspace.Service.GetOverrideChain(id));

    [McpServerTool(Name = "query_records", ReadOnly = true), Description("Winning records of one type, optionally filtered by EditorID.")]
    public IReadOnlyList<RecordSummary> QueryRecords(
        [Description("Mutagen record class name, e.g. LeveledItem, Npc, Weapon.")] string type,
        [Description("Only records whose EditorID contains this text (case-insensitive).")] string? editorIdContains = null,
        int limit = 100) =>
        Guard(() => workspace.Service.QueryRecords(type, editorIdContains, limit));

    [McpServerTool(Name = "find_references", ReadOnly = true), Description("Winning records that link to a record.")]
    public IReadOnlyList<RecordSummary> FindReferences([Description("A FormKey or an EditorID.")] string id, int limit = 100) =>
        Guard(() => workspace.Service.FindReferences(id, limit));

    [McpServerTool(Name = "validate_patch", ReadOnly = true), Description("Compiles a program under the sandbox's API policy and returns any diagnostics.")]
    public ValidationResult ValidatePatch(
        [Description("C# source of the program.")] string source,
        [Description("Optional data-only settings classes, for Synthesis's settings GUI.")] string? settingsSource = null) =>
        Guard(() => AuthoringService.Validate(source, settingsSource));

    [McpServerTool(Name = "test_patch", ReadOnly = true, Destructive = false),
     Description("Runs a program in the sandbox against the load order, as Synthesis would, and reports what would be accepted, field by field. Nothing is written to the load order.")]
    public TestResult TestPatch(
        [Description("C# source of the program.")] string source,
        [Description("Fields overrides may change: Type.Field, Type.* or *.")] string[] writable,
        [Description("Record types the program may create.")] string[]? creatable = null,
        [Description("Record types the program may remove from the patch.")] string[]? removable = null,
        [Description("Loose Data files the program may read, as Data-relative globs such as meshes/**/*.nif.")] string[]? assets = null,
        int maxRecords = 10_000,
        [Description("Optional data-only settings classes.")] string? settingsSource = null,
        [Description("Settings values to run with, as JSON.")] string? settingsJson = null,
        CancellationToken cancel = default) =>
        Guard(() => workspace.Service.TestPatch(source, new PatchScope(writable, creatable, removable, assets, maxRecords),
            settingsSource is null ? null : new SettingsInput(settingsSource, settingsJson), cancel: cancel));

    [McpServerTool(Name = "package_synthesis_patcher", Destructive = false, OpenWorld = false),
     Description("Writes a Synthesis patcher repository for the program into a new, empty folder, with a REVIEW.md for the person publishing it.")]
    public GenerateResult PackageSynthesisPatcher(
        [Description("Patcher name: letters and digits, e.g. CacoLeveledLists.")] string name,
        [Description("C# source of the program.")] string source,
        [Description("An empty or missing folder to write the repository into.")] string outputDirectory,
        [Description("Fields overrides may change: Type.Field, Type.* or *.")] string[] writable,
        string[]? creatable = null,
        string[]? removable = null,
        string[]? assets = null,
        int maxRecords = 10_000,
        string? settingsSource = null,
        [Description("One line shown in Synthesis.")] string? description = null,
        [Description("Plugins the patcher needs, e.g. Complete Alchemy & Cooking Overhaul.esp.")] string[]? requiredMods = null) =>
        Guard(() =>
        {
            var output = Path.GetFullPath(outputDirectory);
            if (workspace.Snapshot is { } snapshot && output.StartsWith(snapshot.DataFolder, StringComparison.OrdinalIgnoreCase))
                throw new McpException("The patcher cannot be written inside the load order's Data folder.");
            return AuthoringService.Package(new PackageRequest(name, source, new PatchScope(writable, creatable, removable, assets, maxRecords), output,
                settingsSource is null ? null : new SettingsInput(settingsSource), description, requiredMods));
        });

    /// <summary>Reports SafePatch's own errors to the client; anything else stays a generic tool error.</summary>
    private static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is SafePatchException or ArgumentException or IOException)
        {
            throw new McpException(e.Message, e);
        }
    }
}
