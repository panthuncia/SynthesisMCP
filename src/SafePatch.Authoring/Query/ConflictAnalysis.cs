using System.Collections;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

/// <summary>How a record's versions relate, as xEdit's conflict colours say.</summary>
public enum RecordStatus
{
    /// <summary>One version: nothing overrides it.</summary>
    Single,
    /// <summary>Every override is identical to the version it overrides.</summary>
    Itm,
    /// <summary>Overridden, and the winner carries every change the overrides made.</summary>
    Override,
    /// <summary>The winner drops changes an override made on purpose (lost edits).</summary>
    Conflict,
    /// <summary>Edits are lost, but the winner masters every plugin that lost them: a patch chose what to keep.</summary>
    Resolved,
}

/// <param name="Base">The version this one was made against: the highest earlier version from one of its plugin's masters.</param>
/// <param name="Intended">The fields this version changed relative to its base: its plugin's intended edits.</param>
public sealed record RecordVersionInfo(ModKey Plugin, IMajorRecordGetter Record, int Base, IReadOnlyList<string> Intended, bool IsItm)
{
    public bool IsDeleted => Record.IsDeleted;
}

/// <summary>An edit the winner does not carry. For a list field, the elements whose addition or removal it loses.</summary>
public sealed record LostEdit(ModKey Plugin, string Field, IReadOnlyList<object>? LostAdded = null, IReadOnlyList<object>? LostRemoved = null);

public sealed record RecordConflicts(IReadOnlyList<RecordVersionInfo> Versions, IReadOnlyList<LostEdit> Lost, RecordStatus Status)
{
    public RecordVersionInfo Winner => Versions[^1];
    public bool IsDeleted => Versions.Any(v => v.IsDeleted);
}

/// <summary>
/// A record's override chain analysed the way conflict detection in xEdit works. Each version is compared with its
/// base (the version its author saw, from one of its plugin's masters) to find what it meant to change, and then
/// with the winner to find which of those changes are lost. Lists are compared element by element, so two plugins
/// each adding entries to one leveled list are told apart from one dropping the other's. Uses Mutagen's generated
/// equals masks, so it needs no code per record type.
/// </summary>
public static class ConflictAnalysis
{
    /// <param name="chain">The record's versions, the original first, with each plugin's masters.</param>
    public static RecordConflicts Analyze(IReadOnlyList<(ModKey Plugin, IMajorRecordGetter Record, IReadOnlySet<ModKey> Masters)> chain)
    {
        var versions = new List<RecordVersionInfo>(chain.Count);
        for (var i = 0; i < chain.Count; i++)
        {
            if (i == 0)
            {
                versions.Add(new RecordVersionInfo(chain[0].Plugin, chain[0].Record, 0, [], false));
                continue;
            }
            var masters = chain[i].Masters;
            var baseIndex = Enumerable.Range(0, i).LastOrDefault(j => masters.Contains(chain[j].Plugin));
            var intended = RecordDiff.ChangedFields(chain[baseIndex].Record, chain[i].Record);
            versions.Add(new RecordVersionInfo(chain[i].Plugin, chain[i].Record, baseIndex, intended, intended.Count == 0));
        }

        var winner = versions[^1];
        var lost = new List<LostEdit>();
        foreach (var version in versions.Skip(1).SkipLast(1))
        {
            if (version.Intended.Count == 0) continue;
            var differs = RecordDiff.ChangedFields(version.Record, winner.Record).ToHashSet(StringComparer.Ordinal);
            foreach (var field in version.Intended.Where(differs.Contains))
            {
                if (Lost(versions[version.Base].Record, version, winner.Record, field) is { } edit) lost.Add(edit);
            }
        }

        var status = versions.Count == 1 ? RecordStatus.Single
            : lost.Count == 0 ? (versions.Skip(1).All(v => v.IsItm) ? RecordStatus.Itm : RecordStatus.Override)
            : lost.All(l => chain[^1].Masters.Contains(l.Plugin)) ? RecordStatus.Resolved
            : RecordStatus.Conflict;
        return new RecordConflicts(versions, lost, status);
    }

    /// <summary>The field's lost edit, or null when a list field's element changes all survive (only order or position differ).</summary>
    private static LostEdit? Lost(IMajorRecordGetter @base, RecordVersionInfo version, IMajorRecordGetter winner, string field)
    {
        var mine = RecordText.FieldValue(version.Record, field);
        if (!ListDiff.IsList(mine) && !ListDiff.IsList(RecordText.FieldValue(@base, field))) return new LostEdit(version.Plugin, field);

        var changes = ListDiff.Compare(RecordText.FieldValue(@base, field) as IEnumerable, mine as IEnumerable);
        var inWinner = RecordText.FieldValue(winner, field) as IEnumerable;
        var lostAdded = ListDiff.Absent(changes.Added, inWinner);
        var keptRemoved = ListDiff.Present(changes.Removed, inWinner);
        return lostAdded.Count == 0 && keptRemoved.Count == 0 ? null : new LostEdit(version.Plugin, field, lostAdded, keptRemoved);
    }
}
