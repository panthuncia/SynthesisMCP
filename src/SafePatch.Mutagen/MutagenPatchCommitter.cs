using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using SafePatch.Host;

namespace SafePatch.Mutagen;

/// <summary>
/// Compares the worker's output plugin with the real patch mod, checks every added, changed or
/// removed record against the policy, and only then applies the accepted records to the patch mod.
/// Generic over games: record types, fields, links and where nested records live all come from
/// Mutagen's generated code.
/// </summary>
/// <param name="patchMod">The Synthesis patch mod the worker started from and whose changes it returns.</param>
/// <param name="linkCache">Resolves the records visible to this patcher, including <paramref name="patchMod"/>.</param>
/// <param name="persistence">Synthesis's FormKey allocation state, when the run uses it.</param>
/// <param name="format">How the run's Synthesis reads and writes plugins, which the worker's output follows.</param>
public sealed class MutagenPatchCommitter<TMod, TModGetter>(
    TMod patchMod, ILinkCache<TMod, TModGetter> linkCache, GameRelease release, FormKeyPersistence? persistence = null, PluginFormat? format = null)
    : IPatchCommitter
    where TModGetter : class, IModGetter, IMajorRecordContextEnumerable<TMod, TModGetter>
    where TMod : class, TModGetter, IMod
{
    private sealed record Accepted(IModContext<TMod, TModGetter, IMajorRecord, IMajorRecordGetter> Output, RecordChange Change);

    /// <summary>A record in the patch mod that the output left out, with how deeply it is nested.</summary>
    private sealed record Removed(IMajorRecordGetter Record, int Depth, RecordChange Change);

    public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? updatedPersistence, PatchPolicy policy)
    {
        using var codec = new ModCodec(release, patchMod.ModKey, format);
        List<Accepted> accepted;
        List<Removed> removed;
        try
        {
            (accepted, removed) = Validate(codec, (TModGetter)codec.Read(outputPlugins), policy);
        }
        catch (Exception e) when (e is not PatchRejectedException)
        {
            // The plugin is untrusted bytes; Mutagen reads it lazily, so malformed data can surface anywhere in validation.
            throw new PatchRejectedException($"The worker's plugin could not be read: {e.GetType().Name}: {e.Message}", inner: e);
        }
        var created = accepted.Where(a => a.Change.IsNew).Select(a => a.Output.Record).ToList();

        IReadOnlyList<(string, uint)>? persisted = null;
        if (updatedPersistence is not null)
        {
            persisted = (persistence ?? throw new PatchRejectedException("The worker returned FormKey persistence, but this run does not use it."))
                .Validate(updatedPersistence, created.ToDictionary(r => r.FormKey, r => r.EditorID), patchMod.ModKey);
        }

        // Parents first, so a child's context finds the parent already in place.
        foreach (var (output, _) in accepted)
            RecordDiff.CopyIn(output.GetOrAddAsOverride(patchMod), output.Record);
        // Children first, so each record is still in place when it is removed.
        foreach (var record in removed.OrderByDescending(r => r.Depth))
            patchMod.Remove(record.Record.FormKey, record.Record.Registration.GetterType);
        // New records keep the FormKeys the worker gave them; later allocations must not reuse them.
        foreach (var record in created)
            patchMod.NextFormID = Math.Max(patchMod.NextFormID, record.FormKey.ID + 1);
        if (persisted is not null) persistence!.Write(persisted);

        return [.. accepted.Select(a => a.Change), .. removed.Select(r => r.Change)];
    }

    private (List<Accepted>, List<Removed>) Validate(ModCodec codec, TModGetter output, PatchPolicy policy)
    {
        var errors = new List<string>();
        var contexts = output.EnumerateMajorRecordContexts<IMajorRecord, IMajorRecordGetter>(linkCache)
            .OrderBy(ModCodec.Depth)
            .ToList();

        var outputRecords = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var context in contexts)
        {
            if (!outputRecords.TryAdd(context.Record.FormKey, context.Record))
                errors.Add($"{Describe(context.Record)}: appears more than once in the patch.");
        }

        var removed = new List<Removed>();
        foreach (var context in patchMod.EnumerateMajorRecordContexts<IMajorRecord, IMajorRecordGetter>(linkCache))
        {
            var record = context.Record;
            if (outputRecords.ContainsKey(record.FormKey)) continue;
            var type = RecordDiff.TypeName(record);
            if (!policy.CanRemove(type)) errors.Add($"{Describe(record)}: removing {type} records is not allowed.");
            removed.Add(new(record, ModCodec.Depth(context), new RecordChange(record.FormKey.ToString(), type, record.EditorID, IsNew: false, [], IsRemoved: true)));
        }

        // Removing an override reverts to the version below it; removing a record the patch added deletes it,
        // so nothing kept may still point at it.
        var deleted = removed.Select(r => r.Record.FormKey).Where(k => k.ModKey == patchMod.ModKey).ToHashSet();
        if (deleted.Count > 0)
        {
            foreach (var record in outputRecords.Values)
            {
                foreach (var link in record.EnumerateFormLinks().Where(l => deleted.Contains(l.FormKey)).Select(l => l.FormKey).Distinct())
                    errors.Add($"{Describe(record)}: links to removed record {link}.");
            }
        }

        // Current versions of everything the output touches, re-read the way the output was read.
        var originals = new Dictionary<FormKey, IModContext<TMod, TModGetter, IMajorRecord, IMajorRecordGetter>>();
        foreach (var record in outputRecords.Values)
        {
            if (linkCache.TryResolveContext(record.FormKey, record.Registration.GetterType, out var original))
                originals[record.FormKey] = original;
        }
        var baselines = codec.RoundTrip(originals.Values).EnumerateMajorRecords().ToDictionary(r => r.FormKey);

        var accepted = new List<Accepted>();
        foreach (var context in contexts)
        {
            var record = context.Record;
            var type = RecordDiff.TypeName(record);

            if (!baselines.TryGetValue(record.FormKey, out var baseline))
            {
                if (record.FormKey.ModKey != patchMod.ModKey)
                    errors.Add($"{Describe(record)}: overrides a record that is not in the load order.");
                else if (!policy.CanCreate(type))
                    errors.Add($"{Describe(record)}: creating {type} records is not allowed.");
                // A new record's parent is in the output too, so it is checked as a record of its own.
                errors.AddRange(NewBrokenLinks(record, original: null, outputRecords).Select(l => $"{Describe(record)}: links to missing record {l}."));
                accepted.Add(new(context, new RecordChange(record.FormKey.ToString(), type, record.EditorID, IsNew: true, [])));
                continue;
            }

            if (ParentKey(context) != ParentKey(originals[record.FormKey]))
                errors.Add($"{Describe(record)}: moving a record to a different parent is not allowed.");

            var changed = RecordDiff.ChangedFields(baseline, record);
            if (changed.Count == 0) continue;
            errors.AddRange(changed.Where(f => !policy.CanWrite(type, f)).Select(f => $"{Describe(record)}: changing {type}.{f} is not allowed."));
            errors.AddRange(NewBrokenLinks(record, originals[record.FormKey].Record, outputRecords).Select(l => $"{Describe(record)}: links to missing record {l}."));
            accepted.Add(new(context, new RecordChange(record.FormKey.ToString(), type, record.EditorID, IsNew: false, changed)));
        }

        if (accepted.Count + removed.Count > policy.MaxRecords)
            errors.Add($"The patch changes {accepted.Count + removed.Count} records, more than the limit of {policy.MaxRecords}.");
        if (errors.Count > 0)
        {
            errors = errors.Distinct().ToList();
            throw new PatchRejectedException($"Patch rejected with {errors.Count} error(s):{Environment.NewLine}{string.Join(Environment.NewLine, errors.Take(50))}");
        }
        return (accepted, removed);
    }

    /// <summary>The record that directly contains this one, if any.</summary>
    private static FormKey? ParentKey(IModContext context)
    {
        for (var parent = context.Parent; parent is not null; parent = parent.Parent)
            if (parent.Record is IMajorRecordGetter record) return record.FormKey;
        return null;
    }

    /// <summary>Links the record gained (relative to <paramref name="original"/>) that resolve nowhere.</summary>
    private IEnumerable<FormKey> NewBrokenLinks(IMajorRecordGetter record, IMajorRecordGetter? original, IReadOnlyDictionary<FormKey, IMajorRecordGetter> outputRecords)
    {
        var existing = original?.EnumerateFormLinks().Select(l => l.FormKey).ToHashSet() ?? [];
        return record.EnumerateFormLinks()
            .Where(l => !l.IsNull && !existing.Contains(l.FormKey) && !outputRecords.ContainsKey(l.FormKey)
                        && !linkCache.TryResolve(l.FormKey, l.Type, out _))
            .Select(l => l.FormKey)
            .Distinct();
    }

    private static string Describe(IMajorRecordGetter record) =>
        record.EditorID is { } id ? $"{record.FormKey} ({id})" : record.FormKey.ToString();
}
