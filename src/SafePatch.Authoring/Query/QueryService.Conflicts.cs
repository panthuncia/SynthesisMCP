using System.Collections;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Index;
using SafePatch.Authoring.Output;
using SafePatch.Host;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

/// <summary>Which records <see cref="QueryService.FindConflicts"/> reports.</summary>
public enum ConflictKind
{
    /// <summary>Lost edits no patch resolved.</summary>
    Conflict,
    /// <summary>Lost edits where the winner masters every plugin that lost them.</summary>
    Resolved,
    /// <summary>Any lost edit, resolved or not.</summary>
    Lost,
    /// <summary>Overrides identical to the version they override.</summary>
    Itm,
    /// <summary>Every overridden record, with its status.</summary>
    All,
}

/// <param name="Types">Record types to scan; <paramref name="Types"/> or <paramref name="Plugin"/> is required.</param>
/// <param name="Plugin">Scan only this plugin's records; for <see cref="ConflictKind.Itm"/>, only its ITMs.</param>
/// <param name="Field">Only lost edits of this field.</param>
/// <param name="Group">A group key from the summary (<c>LeveledItem.Entries: A.esp → B.esp</c>): list its records.</param>
public sealed record ConflictFilter(
    IReadOnlyList<string>? Types = null, string? Plugin = null, ConflictKind Status = ConflictKind.Conflict, string? Field = null, string? Group = null);

public sealed partial class QueryService
{
    private IReadOnlySet<ModKey>[]? _masters;

    /// <summary>
    /// A record's versions side by side, xEdit's conflict view: only fields that differ somewhere in the chain, each
    /// version's value (repeats collapsed to <c>= plugin</c>, lists as the entries each version added and removed), and
    /// which edits the winner loses.
    /// </summary>
    public ResultSet CompareRecord(string id, IReadOnlyList<string>? fields = null) => Timed(() =>
    {
        var analysis = Analyze(Resolve(id));
        var versions = analysis.Versions;
        var winner = analysis.Winner.Record;
        var names = RecordDiff.FieldNames(winner.Registration.ClassType);
        var children = RecordDiff.ChildFields(winner.Registration.ClassType);

        var differing = versions.Skip(1)
            .SelectMany((v, i) => RecordDiff.ChangedFields(versions[i].Record, v.Record).Concat(v.Intended))
            .ToHashSet(StringComparer.Ordinal);
        var shown = names.Where(n => differing.Contains(n) && !children.Contains(n) && n != "MajorRecordFlagsRaw")
            .Where(n => fields is null || fields.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

        var lines = new List<string>
        {
            $"{winner.FormKey} {RecordDiff.TypeName(winner)} {winner.EditorID}: {Describe(analysis)}",
            "Versions: " + string.Join(" → ", versions.Select((v, i) => v.Plugin.FileName + Markers(v, i, versions.Count))),
        };
        if (versions.Count == 1) lines.Add("Nothing overrides this record.");
        else if (shown.Count == 0) lines.Add(fields is null ? "Every version is identical." : "Those fields are identical in every version.");

        foreach (var field in shown)
        {
            lines.Add($"{field}:");
            var isList = versions.Any(v => ListDiff.IsList(RecordText.FieldValue(v.Record, field)));
            for (var i = 0; i < versions.Count; i++)
            {
                var version = versions[i];
                var value = RecordText.FieldValue(version.Record, field);
                var text = isList && i > 0 ? ListChange(versions, i, field) : Shown(versions, i, field, value);
                var lost = analysis.Lost.FirstOrDefault(l => l.Plugin == version.Plugin && l.Field == field);
                lines.Add($"  {version.Plugin.FileName}{(i == versions.Count - 1 ? " (winner)" : "")}: {text}{(lost is null ? "" : "  LOST" + LostText(lost, versions))}");
            }
        }
        return ResultSet.Lines($"Versions of {winner.FormKey}", lines, hint: "name the fields you need with fields");
    });

    /// <summary>
    /// Conflicts in a record type or a plugin. For lost edits, first a summary: how many records lose which field
    /// from which plugin to which winner, each a group key; with <see cref="ConflictFilter.Group"/>, that group's records.
    /// </summary>
    public ResultSet FindConflicts(ConflictFilter filter) => Timed(() =>
    {
        if (filter.Types is not { Count: > 0 } && filter.Plugin is not { Length: > 0 })
        {
            var overridden = Index.Chains.Count(c => c.Versions > 1);
            throw new SafePatchException($"find_conflicts needs types or plugin: analysing all {overridden:N0} overridden records reads every version of each. " +
                                         "Start from load_order to see which plugins override the most, or name the record types your patch touches.");
        }
        var signatures = Signatures(filter.Types);
        var pluginIndex = filter.Plugin is { Length: > 0 } name ? PluginIndex(name) : -1;
        var plugin = pluginIndex < 0 ? null : snapshot.Mods[pluginIndex];
        var analysed = Analysed(signatures, pluginIndex);
        bool FieldMatches(string field) => filter.Field is null || field.Equals(filter.Field, StringComparison.OrdinalIgnoreCase);
        var scope = $"{(filter.Types is { Count: > 0 } ? string.Join(", ", filter.Types) : "records")}{(plugin is null ? "" : $" in {plugin.ModKey}")}";

        if (filter.Status is ConflictKind.Conflict or ConflictKind.Resolved or ConflictKind.Lost)
        {
            var matching = analysed.Where(a => filter.Status switch
            {
                ConflictKind.Conflict => a.Analysis.Status == RecordStatus.Conflict,
                ConflictKind.Resolved => a.Analysis.Status == RecordStatus.Resolved,
                _ => a.Analysis.Lost.Count > 0,
            }).Select(a => (a.Record, a.Analysis, Keys: a.Analysis.Lost.Where(l => FieldMatches(l.Field)).Select(l => GroupKey(a.Record, a.Analysis, l)).Distinct().ToList()))
              .Where(a => a.Keys.Count > 0)
              .ToList();

            if (filter.Group is null)
            {
                var groups = matching.SelectMany(m => m.Keys).GroupBy(k => k).Select(g => (Key: g.Key, Count: g.Count()))
                    .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
                return new ResultSet($"Lost edits ({filter.Status.ToString().ToLowerInvariant()}) in {scope}, {matching.Count:N0} records, by group", ["Group", "Records"],
                    [.. groups.Select(g => (IReadOnlyList<string?>)[g.Key, g.Count.ToString()])],
                    [ResultSet.GroupBy("type", matching.Select(m => RecordDiff.TypeName(m.Record)))],
                    Notes: ["Each group is Type.Field: plugin that lost the edit → winner. Pass one as group to list its records; compare_record shows one in full."]);
            }
            var inGroup = matching.Where(m => m.Keys.Contains(filter.Group.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            return RecordRows($"Records in {filter.Group}", inGroup.Select(m => (m.Record, m.Analysis)), plugin, filter);
        }

        var rows = analysed.Where(a => filter.Status switch
        {
            ConflictKind.Itm => a.Analysis.Versions.Any(v => v.IsItm && (plugin is null || v.Plugin == plugin.ModKey)),
            _ => filter.Field is null || a.Analysis.Versions.Any(v => v.Intended.Any(FieldMatches)),
        });
        return RecordRows(filter.Status == ConflictKind.Itm ? $"ITMs in {scope}" : $"Overridden {scope}", rows, plugin, filter);
    });

    /// <summary>
    /// Every overridden record of some types or a plugin, analysed. An agent reads a summary and then drills into its
    /// groups, each a query over the same records, so the last few scans are kept (they hold only what a result
    /// already needed).
    /// </summary>
    private List<(IMajorRecordGetter Record, RecordConflicts Analysis)> Analysed(IReadOnlySet<uint>? signatures, int pluginIndex)
    {
        var key = (signatures is null ? "" : string.Join(",", signatures.Order()), pluginIndex);
        lock (_analysed)
        {
            if (_analysed.FindIndex(a => a.Key == key) is var hit and >= 0)
            {
                var found = _analysed[hit];
                _analysed.RemoveAt(hit);
                _analysed.Add(found);
                return found.Records;
            }
        }
        var candidates = Index.ParallelChains().Where(c => c.Versions > 1
                                                 && (signatures is null || signatures.Contains(c.Signature))
                                                 && (pluginIndex < 0 || c.Contains(pluginIndex))).ToList();
        // Each record's versions are read and compared independently of every other record's.
        var records = Unwrapped(() => InParallel(candidates).Select(Analyze).Where(a => a.Versions.Count > 1).Select(a => (a.Winner.Record, a)).ToList());
        lock (_analysed)
        {
            _analysed.RemoveAll(a => a.Key == key);
            _analysed.Add((key, records));
            if (_analysed.Count > AnalysesKept) _analysed.RemoveAt(0);
        }
        return records;
    }

    private const int AnalysesKept = 3;
    private readonly List<((string Signatures, int Plugin) Key, List<(IMajorRecordGetter Record, RecordConflicts Analysis)> Records)> _analysed = [];

    internal RecordConflicts Analyze(RecordChain chain)
    {
        _masters ??= [.. Index.Masters.Select(m => (IReadOnlySet<ModKey>)m.ToHashSet())]; // idempotent, so a race only repeats it
        return ConflictAnalysis.Analyze([.. Versions(chain).Select(v => (v.ModKey, v.Record, _masters[v.Plugin]))]);
    }

    private ResultSet RecordRows(string title, IEnumerable<(IMajorRecordGetter Record, RecordConflicts Analysis)> records, ISkyrimModGetter? plugin, ConflictFilter filter)
    {
        var list = records.ToList();
        return new ResultSet(title, ["FormKey", "Type", "EditorID", "Status", "Winner", "Detail"],
            [.. list.Select(r => (IReadOnlyList<string?>)
            [
                r.Record.FormKey.ToString(), RecordDiff.TypeName(r.Record), r.Record.EditorID, r.Analysis.Status + (r.Analysis.IsDeleted ? ", deleted" : ""),
                r.Analysis.Winner.Plugin.FileName, Detail(r.Analysis, plugin, filter),
            ])],
            [ResultSet.GroupBy("type", list.Select(r => RecordDiff.TypeName(r.Record))), ResultSet.GroupBy("status", list.Select(r => r.Analysis.Status.ToString()))],
            Hint: "add types, plugin or field");
    }

    private static string Detail(RecordConflicts analysis, ISkyrimModGetter? plugin, ConflictFilter filter)
    {
        var parts = new List<string>();
        var lost = analysis.Lost.Where(l => filter.Field is null || l.Field.Equals(filter.Field, StringComparison.OrdinalIgnoreCase)).ToList();
        if (lost.Count > 0) parts.Add("lost " + string.Join("; ", lost.Select(l => $"{l.Plugin.FileName} {l.Field}{Counts(l)}")));
        var itm = analysis.Versions.Where(v => v.IsItm && (plugin is null || v.Plugin == plugin.ModKey)).ToList();
        if (itm.Count > 0) parts.Add("ITM in " + string.Join(", ", itm.Select(v => v.Plugin.FileName)));
        if (parts.Count == 0) parts.Add("changed " + string.Join(", ", analysis.Versions.SelectMany(v => v.Intended).Where(f => f != "MajorRecordFlagsRaw").Distinct()));
        return string.Join(" | ", parts);
    }

    private static string GroupKey(IMajorRecordGetter record, RecordConflicts analysis, LostEdit lost) =>
        $"{RecordDiff.TypeName(record)}.{lost.Field}: {lost.Plugin.FileName} → {analysis.Winner.Plugin.FileName}";

    private static string Describe(RecordConflicts analysis)
    {
        var text = analysis.Status switch
        {
            RecordStatus.Single => "single version",
            RecordStatus.Itm => "overridden, every override identical to its master (ITM)",
            RecordStatus.Override => "overridden, no edits lost",
            RecordStatus.Conflict => "CONFLICT, the winner loses " + LostSummary(analysis),
            _ => "resolved by the winner, which masters the plugins whose edits it drops: " + LostSummary(analysis),
        };
        return analysis.IsDeleted ? text + "; a version is DELETED" : text;
    }

    private static string LostSummary(RecordConflicts analysis) =>
        string.Join(", ", analysis.Lost.GroupBy(l => l.Plugin).Select(g => $"{g.Key.FileName} ({string.Join(", ", g.Select(l => l.Field))})"));

    private static string Markers(RecordVersionInfo version, int index, int count)
    {
        var marks = new List<string>();
        if (index == count - 1) marks.Add("winner");
        if (version.IsItm) marks.Add("ITM");
        if (version.IsDeleted) marks.Add("deleted");
        return marks.Count == 0 ? "" : $" ({string.Join(", ", marks)})";
    }

    /// <summary>A scalar field's value, or <c>= plugin</c> when an earlier version already had it.</summary>
    private string Shown(IReadOnlyList<RecordVersionInfo> versions, int index, string field, object? value)
    {
        var text = RecordText.Value(value, EditorIdOf) ?? "(none)";
        for (var j = 0; j < index; j++)
        {
            if ((RecordText.Value(RecordText.FieldValue(versions[j].Record, field), EditorIdOf) ?? "(none)") == text) return $"= {versions[j].Plugin.FileName}";
        }
        return text;
    }

    /// <summary>A list field as the entries a version added to and removed from its base, or <c>= base</c>.</summary>
    private string ListChange(IReadOnlyList<RecordVersionInfo> versions, int index, string field)
    {
        var version = versions[index];
        var @base = versions[version.Base];
        var change = ListDiff.Compare(RecordText.FieldValue(@base.Record, field) as IEnumerable, RecordText.FieldValue(version.Record, field) as IEnumerable);
        if (change.IsEmpty) return $"= {@base.Plugin.FileName}";
        var items = change.Added.Select(a => "+" + RecordText.Value(a, EditorIdOf, depth: 1)).Concat(change.Removed.Select(r => "-" + RecordText.Value(r, EditorIdOf, depth: 1)));
        return $"vs {@base.Plugin.FileName}: {string.Join("; ", items)}";
    }

    private string LostText(LostEdit lost, IReadOnlyList<RecordVersionInfo> versions)
    {
        if (lost.LostAdded is null) return "";
        var version = versions.First(v => v.Plugin == lost.Plugin);
        var change = ListDiff.Compare(RecordText.FieldValue(versions[version.Base].Record, lost.Field) as IEnumerable, RecordText.FieldValue(version.Record, lost.Field) as IEnumerable);
        if (lost.LostAdded.Count == change.Added.Count && (lost.LostRemoved?.Count ?? 0) == change.Removed.Count) return " (all of it)";
        var items = lost.LostAdded.Select(a => "+" + RecordText.Value(a, EditorIdOf, depth: 1))
            .Concat((lost.LostRemoved ?? []).Select(r => "-" + RecordText.Value(r, EditorIdOf, depth: 1) + " (back in the winner)"));
        return ": " + string.Join("; ", items);
    }

    private static string Counts(LostEdit lost) =>
        lost.LostAdded is null ? "" : $" ({lost.LostAdded.Count} added, {lost.LostRemoved?.Count ?? 0} removed entries lost)";
}
