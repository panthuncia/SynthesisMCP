using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SafePatch.Authoring;
using SafePatch.Authoring.Output;
using SafePatch.Authoring.Query;
using SafePatch.Generator;
using SafePatch.Host;

namespace SafePatch.Mcp;

/// <summary>
/// The authoring operations as MCP tools, one to one. Load-order tools answer within a character budget and keep
/// their full result under a handle (<c>read_results</c>, <c>export_results</c>). Programs are native Synthesis patch
/// functions (<c>public static void RunPatch(IPatcherState&lt;ISkyrimMod, ISkyrimModGetter&gt; state)</c>); record types
/// and fields use Mutagen's class and property names, e.g. <c>LeveledItem.Entries</c>.
/// </summary>
[McpServerToolType]
public sealed class SafePatchTools(AuthoringSession session)
{
    public const string Instructions = """
        SafePatch reads the user's Skyrim load order (read-only) and writes sandboxed Synthesis patchers.
        Plan from the load order, not from memory: start broad and drill in.
        1. load_order for the plugins; load_order with a plugin for what it adds and overrides.
        2. find_conflicts with the record types your patch touches: a summary of lost edits (which plugin loses which
           field to which winner), each a group you can list. compare_record shows one record's versions side by side.
        3. find_records, get_record, references, find_asset and describe_type for detail; run_query for anything else,
           as a C# RunPatch program that prints its answer.
        4. validate_patch, then test_patch against the load order, then package_synthesis_patcher.
        Results are summarised to fit a budget (characters, default 8000): the first line gives the total and a handle.
        Page with read_results, raise budget for more at once, or export_results to a file. If a result warns that the
        load order changed, call reload.
        """;

    [McpServerTool(Name = "load_order", ReadOnly = true),
     Description("The plugins in load order, with flags, masters, record counts and missing masters. With plugin: that plugin's header and its new records and overrides by type.")]
    public string LoadOrder(
        [Description("A plugin file name, e.g. Complete Alchemy & Cooking Overhaul.esp.")] string? plugin = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.LoadOrder(plugin), budget));

    [McpServerTool(Name = "find_records", ReadOnly = true),
     Description("Records matching filters, each by its winning version: FormKey, type, EditorID, name, winning plugin and version count. Groups by type and winner.")]
    public string FindRecords(
        [Description("Record types (Mutagen class names such as LeveledItem, Npc, Weapon) or link interfaces (Item). All types if omitted.")] string[]? types = null,
        [Description("Only records in this plugin.")] string? plugin = null,
        [Description("With plugin: any (default), defines, overrides or wins.")] string? role = null,
        [Description("EditorID glob, case-insensitive: * any text, ? one character, e.g. *Iron*.")] string? editorId = null,
        [Description("Text the record's name contains.")] string? name = null,
        [Description("Field conditions joined by and: Field op value, ops == != < <= > >= contains exists. E.g. Value > 100 and Keywords contains ArmorHeavy.")] string? where = null,
        [Description("Only records linking to this record (FormKey or EditorID).")] string? linksTo = null,
        [Description("Only records with at least this many versions (2 = overridden).")] int minVersions = 0,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.FindRecords(new RecordFilter(types, plugin, AuthoringSession.Option(role, PluginRole.Any), editorId, name, where, linksTo, minVersions)), budget));

    [McpServerTool(Name = "get_record", ReadOnly = true),
     Description("One version of a record (the winner by default): its set fields one per line, links named by EditorID, its versions and assets. Long lists are cut unless named in fields.")]
    public string GetRecord(
        [Description("A FormKey such as 012E49:Skyrim.esm, or an exact EditorID.")] string id,
        [Description("The plugin whose version to show; the winner by default.")] string? plugin = null,
        [Description("Only these fields, shown in full, e.g. [\"Entries\"].")] string[]? fields = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.GetRecord(id, plugin, fields), budget));

    [McpServerTool(Name = "compare_record", ReadOnly = true),
     Description("A record's versions side by side (xEdit's conflict view): only fields that differ, each plugin's value, list entries each version added or removed, ITMs, and the edits the winner loses.")]
    public string CompareRecord(
        [Description("A FormKey or an exact EditorID.")] string id,
        [Description("Only these fields.")] string[]? fields = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.CompareRecord(id, fields), budget));

    [McpServerTool(Name = "find_conflicts", ReadOnly = true),
     Description("Conflicts in some record types or one plugin (one of them is required). For lost edits, a summary of groups 'Type.Field: loser → winner' with record counts; pass group to list a group's records.")]
    public string FindConflicts(
        [Description("Record types to scan, e.g. [\"LeveledItem\", \"Npc\"].")] string[]? types = null,
        [Description("Scan this plugin's records instead (or as well).")] string? plugin = null,
        [Description("conflict (default: lost edits no patch resolves), resolved, lost (both), itm, or all (every overridden record).")] string? status = null,
        [Description("Only lost edits of this field, e.g. Entries.")] string? field = null,
        [Description("A group from the summary, to list its records.")] string? group = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.FindConflicts(new ConflictFilter(types, plugin, AuthoringSession.Option(status, ConflictKind.Conflict), field, group)), budget));

    [McpServerTool(Name = "references", ReadOnly = true),
     Description("Records a record links to (out) and records whose winning version links to it (in). Incoming links come from a fast search of every plugin that masters the record's plugin.")]
    public string References(
        [Description("A FormKey or an exact EditorID.")] string id,
        [Description("both (default), out or in.")] string? direction = null,
        [Description("Only incoming links from these record types.")] string[]? types = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.References(id, AuthoringSession.Option(direction, LinkDirection.Both), types), budget));

    [McpServerTool(Name = "find_asset", ReadOnly = true),
     Description("Where a Data file comes from: each loose copy (and its MO2 mod) and each loaded archive holding it, the winner first; then the records that use it.")]
    public string FindAsset(
        [Description("A Data-relative path, e.g. meshes\\armor\\iron\\cuirass.nif.")] string path,
        [Description("Only users of these record types, e.g. [\"Armor\", \"ArmorAddon\"].")] string[]? recordTypes = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.FindAsset(path, recordTypes), budget));

    [McpServerTool(Name = "describe_type", ReadOnly = true),
     Description("With no type, every record type and how many records of it the load order has. With a type, its fields as programs, manifest scopes (writable) and where conditions name them, with their types.")]
    public string DescribeType(
        [Description("A record type, e.g. LeveledItem.")] string? type = null,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.Present(session.Queries.DescribeType(type), budget));

    [McpServerTool(Name = "run_query", ReadOnly = true, Destructive = false),
     Description("Runs a read-only C# query in the sandbox against the load order: a RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) program that prints its answer with Console.WriteLine. Same API and policy as a patch; changes to the patch are discarded.")]
    public string RunQuery(
        [Description("C# source of the query program.")] string source,
        [Description("Loose Data files it may read, as globs such as meshes/**/*.nif. Archives are always readable.")] string[]? assets = null,
        [Description(BudgetText)] int? budget = null,
        CancellationToken cancel = default) =>
        Guard(() => session.Present(session.Service.RunQuery(source, assets, cancel: cancel), budget));

    [McpServerTool(Name = "read_results", ReadOnly = true),
     Description("More of an earlier result, by its handle (e.g. r3), from a row or line offset.")]
    public string ReadResults(
        [Description("The handle from a result's first line.")] string handle,
        [Description("The first row or line to show, from 0.")] int offset = 0,
        [Description(BudgetText)] int? budget = null) =>
        Guard(() => session.ReadResults(handle, offset, budget));

    [McpServerTool(Name = "export_results", Destructive = false, OpenWorld = false),
     Description("Writes a whole earlier result to a new file, for analysis with other tools. Never inside the load order's folders.")]
    public string ExportResults(
        [Description("The handle from a result's first line.")] string handle,
        [Description("A new file to write.")] string path,
        [Description("jsonl (default: one JSON object per row), csv or text.")] string? format = null) =>
        Guard(() => session.Export(handle, path, AuthoringSession.Option(format, ExportFormat.Jsonl)));

    [McpServerTool(Name = "reload", ReadOnly = true),
     Description("Reads the load order again, after the user changed mods, plugins or their order. Earlier result handles stop working.")]
    public string Reload() => Guard(session.Reload);

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
        Guard(() => session.Service.TestPatch(source, new PatchScope(writable, creatable, removable, assets, maxRecords),
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
            session.GuardOutput(outputDirectory);
            return AuthoringService.Package(new PackageRequest(name, source, new PatchScope(writable, creatable, removable, assets, maxRecords), Path.GetFullPath(outputDirectory),
                settingsSource is null ? null : new SettingsInput(settingsSource), description, requiredMods));
        });

    private const string BudgetText = "Most characters to return (default 8000, up to 60000). Larger results are summarised and paged.";

    /// <summary>Reports SafePatch's own errors to the client; anything else stays a generic tool error.</summary>
    internal static T Guard<T>(Func<T> operation)
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

/// <summary>Records and kept results as resources, for clients that attach them (e.g. an @-mention in Claude Code).</summary>
[McpServerResourceType]
public sealed class SafePatchResources(AuthoringSession session)
{
    [McpServerResource(UriTemplate = "safepatch://record/{id}", Name = "record", MimeType = "text/plain"),
     Description("A record's winning version, by FormKey (012E49:Skyrim.esm) or EditorID.")]
    public string Record(string id) => SafePatchTools.Guard(() => Budgeted.Render(session.Queries.GetRecord(Uri.UnescapeDataString(id)), budget: Budgeted.MaxBudget));

    [McpServerResource(UriTemplate = "safepatch://results/{handle}", Name = "results", MimeType = "text/plain"),
     Description("A kept query result, in full.")]
    public string Results(string handle) => SafePatchTools.Guard(() => Budgeted.Render(session.Results.Get(handle, session.Snapshot.Generation), budget: int.MaxValue));
}
