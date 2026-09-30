using System.Collections.Concurrent;
using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Index;
using SafePatch.Authoring.Output;
using SafePatch.Host;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

/// <summary>Which of a plugin's records a query takes.</summary>
public enum PluginRole
{
    /// <summary>Every record the plugin has: its own and its overrides.</summary>
    Any,
    /// <summary>Records the plugin defines (their FormKeys are its own).</summary>
    Defines,
    /// <summary>The plugin's overrides of other plugins' records.</summary>
    Overrides,
    /// <summary>Records whose winning version is the plugin's.</summary>
    Wins,
}

/// <param name="Types">Record types (<c>LeveledItem</c>) or link interfaces (<c>Item</c>); all types when empty.</param>
/// <param name="EditorId">A glob on the EditorID: <c>*</c> any text, <c>?</c> one character, case-insensitive.</param>
/// <param name="Name">Text the record's name contains.</param>
/// <param name="Where">Field conditions, see <see cref="Predicate"/>.</param>
/// <param name="LinksTo">Only records linking to this record (a FormKey or EditorID).</param>
/// <param name="MinVersions">Only records with at least this many versions (2: overridden at least once).</param>
public sealed record RecordFilter(
    IReadOnlyList<string>? Types = null, string? Plugin = null, PluginRole Role = PluginRole.Any, string? EditorId = null,
    string? Name = null, string? Where = null, string? LinksTo = null, int MinVersions = 0);

/// <summary>
/// Read-only queries over a load order snapshot, answering what xEdit shows: plugins, records, their versions,
/// conflicts, references and assets. Every result is a <see cref="ResultSet"/>, rendered within a budget by the
/// front end. Records are picked from the load order index (<see cref="LoadOrderIndex"/>: every record's header and
/// EditorID, read once), and Mutagen reads only the versions a result shows, from the plugin that holds each. Results
/// report how long they took.
/// </summary>
public sealed partial class QueryService(LoadOrderSnapshot snapshot)
{
    private const int ListItemsShown = 10;

    private ILinkCache<ISkyrimMod, ISkyrimModGetter> Links => snapshot.LinkCache;

    private LoadOrderIndex Index => snapshot.Index;

    /// <summary>The load order these queries read.</summary>
    public LoadOrderSnapshot Snapshot => snapshot;

    /// <summary>The load order, or with <paramref name="plugin"/>, one plugin's header and records by type.</summary>
    public ResultSet LoadOrder(string? plugin = null) => Timed(() => plugin is null ? Plugins() : PluginDetail(Plugin(plugin)));

    /// <summary>Records matching a filter, each shown by its winning version (which the conditions test).</summary>
    public ResultSet FindRecords(RecordFilter filter) => Timed(() =>
    {
        var signatures = Signatures(filter.Types);
        var where = filter.Where is { Length: > 0 } text ? Predicate.Parse(text, FormKeyOfEditorId) : null;
        foreach (var getter in (filter.Types ?? []).Select(RecordTypes.Getter).Where(g => RecordTypes.All.Contains(TypeName(g)))) where?.Validate(getter);
        var editorId = filter.EditorId is { Length: > 0 } glob ? new EditorIdGlob(glob) : null;
        var linksTo = filter.LinksTo is { Length: > 0 } target ? Resolve(target).FormKey : (FormKey?)null;
        var plugin = filter.Plugin is { Length: > 0 } name ? PluginIndex(name) : -1;

        // Everything the index knows is checked first; Mutagen reads only the records that pass.
        var chains = Index.ParallelChains()
            .Where(c => signatures is null || signatures.Contains(c.Signature))
            .Where(c => plugin < 0 || InRole(c, plugin, filter.Role))
            .Where(c => editorId is null || c.EditorIdMatches(editorId))
            .Where(c => c.Versions >= filter.MinVersions)
            .ToList();
        // Some signatures hold several classes (GMST: float, int and string settings): the record says which.
        var getters = (filter.Types ?? []).Select(RecordTypes.Getter).ToList();
        var decodes = filter.Name is { Length: > 0 } || where is not null || linksTo is not null;
        // Decoding the winners is most of the work, and each is independent.
        var matches = Unwrapped(() => InParallel(chains)
            .Select(c => (Chain: c, Record: Winner(c)))
            .Where(m => !decodes || m.Record is not null)
            .Where(m => getters.Count == 0 || !RecordTypes.IsShared(m.Chain.Signature) || getters.Any(g => g.IsInstanceOfType(m.Record)))
            .Where(m => filter.Name is not { Length: > 0 } || (NameOf(m.Record!)?.Contains(filter.Name, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(m => linksTo is null || m.Record!.EnumerateFormLinks().Any(l => l.FormKey == linksTo))
            .Where(m => where is null || where.Matches(m.Record!))
            .ToList());

        var title = $"{(filter.Types is { Count: > 0 } ? string.Join(", ", filter.Types) : "All")} records"
                    + (plugin < 0 ? "" : $" {Role(filter.Role)} {Index.Plugins[plugin]}");
        return new ResultSet(title, ["FormKey", "Type", "EditorID", "Name", "Winner", "Versions"],
            [.. matches.Select(m => (IReadOnlyList<string?>)[m.Chain.FormKey.ToString(), TypeOf(m.Chain, m.Record), m.Chain.EditorId, m.Record is null ? null : NameOf(m.Record), Index.Plugins[m.Chain.Winner].FileName, m.Chain.Versions.ToString()])],
            [ResultSet.GroupBy("type", matches.Select(m => TypeOf(m.Chain, m.Record))), ResultSet.GroupBy("winner", matches.Select(m => Index.Plugins[m.Chain.Winner].FileName.String))],
            Hint: "add types, plugin, editorId or where");
    });

    /// <summary>
    /// One version of a record, the winner by default: its set fields, one per line, with links named by EditorID.
    /// Long lists are cut to their first items unless named in <paramref name="fields"/>.
    /// </summary>
    public ResultSet GetRecord(string id, string? plugin = null, IReadOnlyList<string>? fields = null) => Timed(() =>
    {
        var chain = Versions(Resolve(id));
        var version = plugin is null
            ? chain[^1]
            : chain.FirstOrDefault(c => c.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase))
              ?? throw new SafePatchException($"{plugin} has no version of {id}. Versions: {string.Join(", ", chain.Select(c => c.ModKey.FileName))}.");
        var record = version.Record;
        var classType = record.Registration.ClassType;
        var names = RecordDiff.FieldNames(classType);
        var children = RecordDiff.ChildFields(classType);
        foreach (var field in fields ?? [])
        {
            if (!names.Contains(field, StringComparer.OrdinalIgnoreCase))
                throw new SafePatchException($"{classType.Name} has no field {field}. Its fields: {string.Join(", ", names)}.");
        }

        var lines = new List<string>
        {
            $"{record.FormKey} {classType.Name} {record.EditorID} (version from {version.ModKey.FileName}{(version == chain[^1] ? ", the winner" : "")})",
            $"Versions: {string.Join(" → ", chain.Select(c => c.ModKey.FileName))}",
        };
        if (AssetPaths(record).ToList() is { Count: > 0 } assets) lines.Add($"Assets: {string.Join(", ", assets)}");
        foreach (var name in names.Where(n => fields is null || fields.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            if (name is "EditorID" or "MajorRecordFlagsRaw" && fields is null) continue;
            var value = RecordText.FindProperty(record, name)?.GetValue(record);
            if (children.Contains(name))
            {
                if (value is System.Collections.IEnumerable items && Count(items) is > 0 and var n) lines.Add($"{name}: {n} child records (query them by type, e.g. find_records)");
                continue;
            }
            lines.AddRange(FieldLines(name, value, full: fields is not null));
        }
        return ResultSet.Lines($"Record {record.FormKey}", lines, hint: "name the fields you need with fields");
    });

    /// <summary>With no type, every record type; with one, its fields (as programs, scopes and conditions name them) and their types.</summary>
    public ResultSet DescribeType(string? type = null) => Timed(() =>
    {
        if (type is null)
        {
            // Records count under their class: a shared signature (game settings, globals) is read to learn which, and a
            // signature Mutagen does not model is listed as itself.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in Index.Chains.GroupBy(c => c.Signature))
            {
                var names = RecordTypes.IsShared(group.Key)
                    ? group.Select(c => Winner(c) is { } record ? RecordDiff.TypeName(record) : PluginScanner.Name(group.Key))
                    : Enumerable.Repeat(TypeOf(group.First()), group.Count());
                foreach (var name in names) counts[name] = counts.GetValueOrDefault(name) + 1;
            }
            var rows = RecordTypes.All.Union(counts.Keys)
                .Select(t => (Type: t, Count: counts.GetValueOrDefault(t)))
                .OrderByDescending(t => t.Count).ThenBy(t => t.Type, StringComparer.Ordinal);
            return new ResultSet("Record types", ["Type", "Records"], [.. rows.Select(t => (IReadOnlyList<string?>)[t.Type, t.Count.ToString()])],
                Notes: ["Link interfaces (Item, Placeable, Npc spawns...) also work as find_records types, e.g. Item for every item type."]);
        }
        var classType = RecordTypes.Class(type);
        return new ResultSet($"{classType.Name} fields", ["Field", "Type", "Details"], [.. TypeDescription.Rows(classType)],
            Notes: ["Conditions (where) reach nested fields with dots, e.g. Data.Damage; lists support contains and .Count."]);
    });

    // ---- shared helpers ----

    /// <summary>One plugin's version of a record.</summary>
    internal sealed record Version(ModKey ModKey, int Plugin, IMajorRecordGetter Record);

    /// <summary>Adds timing, the snapshot's generation and a stale warning to a result.</summary>
    private ResultSet Timed(Func<ResultSet> query)
    {
        var watch = Stopwatch.StartNew();
        var set = query();
        var notes = snapshot.StaleReason() is { } stale
            ? [$"Warning: {stale}; call reload to read the load order again.", .. set.Notes ?? []]
            : set.Notes;
        return set with { Notes = notes, Elapsed = watch.Elapsed, Generation = snapshot.Generation };
    }

    /// <summary>A record by FormKey (<c>012E49:Skyrim.esm</c>) or EditorID (its winning one).</summary>
    internal RecordChain Resolve(string id)
    {
        var text = id.Trim();
        var chain = FormKey.TryFactory(text, out var formKey) ? Index.Find(formKey) : Index.FindEditorId(text);
        return chain ?? throw new SafePatchException($"No record {id} in the load order. Give a FormKey (012E49:Skyrim.esm) or an exact EditorID; find_records searches.");
    }

    /// <summary>Every version of a record, the original first, each read from where the index found it.</summary>
    internal IReadOnlyList<Version> Versions(RecordChain chain) =>
        [.. Enumerable.Range(0, chain.Versions).Select(v => snapshot.Read(chain, v) is { } record ? new Version(Index.Plugins[chain.PluginAt(v)], chain.PluginAt(v), record) : null).OfType<Version>()];

    /// <summary>
    /// Items to read records for, in parallel and in order. They are handed out in small chunks as threads ask, since
    /// some take far longer to read than others.
    /// </summary>
    internal static ParallelQuery<T> InParallel<T>(List<T> items) => Partitioner.Create(items, loadBalance: true).AsParallel().AsOrdered();

    /// <summary>Runs a parallel query, throwing what failed in it as itself rather than wrapped, as a sequential query would.</summary>
    internal static T Unwrapped<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (AggregateException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>The winning version, or null for a record type Mutagen does not read.</summary>
    public IMajorRecordGetter? Winner(RecordChain chain) => snapshot.Read(chain, chain.Versions - 1);

    /// <summary>A plugin in the load order, by file name.</summary>
    internal ISkyrimModGetter Plugin(string name) => snapshot.Mods[PluginIndex(name)];

    internal int PluginIndex(string name)
    {
        for (var i = 0; i < snapshot.Mods.Count; i++)
        {
            if (snapshot.Mods[i].ModKey.FileName.String.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new SafePatchException($"{name} is not a plugin in the load order (load_order lists them).");
    }

    /// <summary>The signatures record type names cover, or null for all.</summary>
    internal static IReadOnlySet<uint>? Signatures(IReadOnlyList<string>? types) =>
        types is { Count: > 0 } ? types.SelectMany(RecordTypes.Signatures).ToHashSet() : null;

    internal string? EditorIdOf(FormKey formKey) => Index.Find(formKey)?.EditorId;

    private FormKey? FormKeyOfEditorId(string editorId) => Index.FindEditorId(editorId)?.FormKey;

    /// <summary>A record's type: Mutagen's class name (the record's own when read), or the signature for one Mutagen does not model.</summary>
    internal static string TypeOf(RecordChain chain, IMajorRecordGetter? record = null) =>
        record is not null ? RecordDiff.TypeName(record) : RecordTypes.ClassOf(chain.Signature)?.Name ?? PluginScanner.Name(chain.Signature);

    /// <summary>Whether a plugin has a version of a record in a role: defining it (the FormKey is its own), overriding it, or winning.</summary>
    private bool InRole(RecordChain chain, int plugin, PluginRole role)
    {
        if (!chain.Contains(plugin)) return false;
        var defines = chain.FormKey.ModKey == Index.Plugins[plugin];
        return role switch
        {
            PluginRole.Defines => defines,
            PluginRole.Overrides => !defines,
            PluginRole.Wins => chain.Winner == plugin,
            _ => true,
        };
    }

    private ResultSet Plugins()
    {
        var present = snapshot.Mods.ToDictionary(m => m.ModKey);
        var listed = snapshot.LoadOrder.ListedOrder.Select(l => l.ModKey).ToList();
        var rows = listed.Select((key, index) =>
        {
            present.TryGetValue(key, out var mod);
            var missing = mod?.MasterReferences.Select(m => m.Master).Where(m => !present.ContainsKey(m) || listed.IndexOf(m) > index).ToList() ?? [];
            return (IReadOnlyList<string?>)
            [
                index.ToString(), key.FileName, mod is null ? "not found" : Flags(mod), mod?.MasterReferences.Count.ToString(),
                mod?.ModHeader.Stats.NumRecords.ToString(), missing.Count == 0 ? null : string.Join(", ", missing.Select(m => m.FileName)),
            ];
        });
        var notes = new List<string> { $"From {snapshot.Resolved.Description}." };
        notes.AddRange(snapshot.Resolved.Warnings);
        return new ResultSet("Load order", ["#", "Plugin", "Flags", "Masters", "Records", "Missing masters"], [.. rows], Notes: notes,
            Hint: "load_order with plugin shows one plugin's detail");
    }

    private ResultSet PluginDetail(ISkyrimModGetter mod)
    {
        var index = PluginIndex(mod.ModKey.FileName);
        var file = snapshot.View.TopFiles.GetValueOrDefault(mod.ModKey.FileName.String);
        var present = snapshot.Mods.Take(index).Select(m => m.ModKey).ToHashSet();
        var notes = new List<string>
        {
            $"{mod.ModKey.FileName}: position {index}, {Flags(mod)}" + (file is null ? "" : $", from {file.Layer.Name} ({file.RealPath})"),
            $"Masters: {(mod.MasterReferences.Count == 0 ? "none" : string.Join(", ", mod.MasterReferences.Select(m => m.Master.FileName + (present.Contains(m.Master) ? "" : " (missing)"))))}",
        };
        if (mod.ModHeader.Author is { Length: > 0 } author) notes.Add($"Author: {author}");
        if (mod.ModHeader.Description is { Length: > 0 } description) notes.Add($"Description: {description.ReplaceLineEndings(" ")}");

        var counts = Index.TypeCounts[index]
            .Select(c => (Type: RecordTypes.ClassOf(c.Signature)?.Name ?? PluginScanner.Name(c.Signature), c.New, c.Overrides))
            .OrderBy(c => c.Type, StringComparer.Ordinal)
            .ToList();
        notes.Add($"Records: {counts.Sum(c => c.New):N0} new, {counts.Sum(c => c.Overrides):N0} overrides.");
        return new ResultSet($"{mod.ModKey.FileName} records by type", ["Type", "New", "Overrides"],
            [.. counts.Select(c => (IReadOnlyList<string?>)[c.Type, c.New.ToString(), c.Overrides.ToString()])], Notes: notes);
    }

    /// <summary>A field as lines: <c>Name: value</c>, or a list's count then one item per line.</summary>
    private IEnumerable<string> FieldLines(string name, object? value, bool full)
    {
        if (!full && RecordText.IsDefault(value)) yield break;
        if (ListDiff.IsList(value) && value is System.Collections.IEnumerable items)
        {
            var all = items.Cast<object?>().ToList();
            if (all.Count == 0) yield break;
            if (all.All(i => i is byte))
            {
                yield return $"{name}: {RecordText.Value(value)}";
                yield break;
            }
            yield return $"{name} ({all.Count}):";
            var shown = full ? all : all.Take(ListItemsShown).ToList();
            foreach (var item in shown) yield return $"  - {RecordText.Value(item, EditorIdOf, depth: 1) ?? "null"}";
            if (shown.Count < all.Count) yield return $"  … +{all.Count - shown.Count} more (get_record with fields: [\"{name}\"] lists all)";
            yield break;
        }
        if (RecordText.Value(value, EditorIdOf) is { } text && (full || text != "{}")) yield return $"{name}: {text}";
    }

    private static int Count(System.Collections.IEnumerable items) => items.Cast<object?>().Count();

    private static string Flags(ISkyrimModGetter mod)
    {
        var flags = new List<string> { mod.ModKey.Type.ToString().ToUpperInvariant() };
        if (mod.IsMaster && mod.ModKey.Type != ModType.Master) flags.Add("ESM-flagged");
        if (mod.IsSmallMaster) flags.Add("ESL-flagged");
        if (mod.UsingLocalization) flags.Add("localized");
        return string.Join(" ", flags);
    }

    private static string? NameOf(IMajorRecordGetter record) =>
        RecordText.FindProperty(record, "Name") is { } property ? RecordText.Value(property.GetValue(record)) : null;

    private static string TypeName(Type getter) => getter.Name.StartsWith('I') && getter.Name.EndsWith("Getter", StringComparison.Ordinal) ? getter.Name[1..^6] : getter.Name;

    private static string Role(PluginRole role) => role switch
    {
        PluginRole.Defines => "defined by",
        PluginRole.Overrides => "overridden by",
        PluginRole.Wins => "won by",
        _ => "in",
    };
}
