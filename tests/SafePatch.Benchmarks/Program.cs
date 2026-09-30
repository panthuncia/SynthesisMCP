using System.Collections.Concurrent;
using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring;
using SafePatch.Authoring.Index;
using SafePatch.Authoring.Sources;
using SafePatch.TestSupport;

// Load-order read benchmarks.
//   write-scale <folder> [plugins=2000] [records=1] [--light]   writes the scale fixture: <folder>\Data and <folder>\plugins.txt
//   bench <Data folder> <plugins.txt> [runs=3]         times each way of reading the load order
return args switch
{
    ["write-scale", var folder, .. var rest] => WriteScale(folder, rest),
    ["bench", var data, var plugins, .. var rest] => Bench(data, plugins, rest is [var runs] ? int.Parse(runs) : 3),
    ["check-types"] => CheckTypes(),
    ["session", var data, var plugins] => Session(data, plugins),
    ["session-scale", var data, var plugins] => SessionScale(data, plugins),
    ["check-glob", var data, var plugins] => CheckGlob(data, plugins),
    ["check-links", var data, var plugins] => CheckLinks(data, plugins),
    ["link-types"] => LinkTypesTiming(),
    ["diff-record", var data, var plugin, .. var keys] => DiffRecord(data, plugin, keys),
    ["self-diff", var data, var plugin, var type] => SelfDiff(data, plugin, type),
    ["check-lists", var data, var plugin] => CheckLists(data, plugin),
    ["check-full", var data, var plugins, var plugin] => CheckFull(data, plugins, plugin),
    ["check-decode", var data, var plugins, .. var rest] => CheckDecode(data, plugins, rest is [var n] ? int.Parse(n) : 40),
    ["probe-api"] => ProbeApi(),
    ["fetch", var data, var plugins, var type] => TimeFetch(data, plugins, type),
    ["check-refs", var data, var plugins, var id] => CheckRefs(data, plugins, id),
    _ => Usage(),
};

// Every list field of every record, read twice: ListDiff must find no element added or removed.
static int CheckLists(string data, string pluginName)
{
    var path = new ModPath(Path.Combine(data, pluginName));
    var first = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
    var second = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).EnumerateMajorRecords()
        .GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
    long lists = 0, elements = 0;
    var differing = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var record in first.EnumerateMajorRecords())
    {
        var again = second[record.FormKey];
        var classType = ((Loqui.ILoquiObject)record).Registration.ClassType;
        var children = SafePatch.Mutagen.RecordDiff.ChildFields(classType);
        foreach (var field in SafePatch.Mutagen.RecordDiff.FieldNames(classType))
        {
            if (children.Contains(field)) continue;
            var a = RecordText.FieldValue(record, field);
            if (!SafePatch.Mutagen.ListDiff.IsList(a)) continue;
            lists++;
            elements += ((System.Collections.IEnumerable)a!).Cast<object?>().Count();
            if (!SafePatch.Mutagen.ListDiff.Compare(a as System.Collections.IEnumerable, RecordText.FieldValue(again, field) as System.Collections.IEnumerable).IsEmpty)
            {
                var name = $"{classType.Name}.{field}";
                differing[name] = differing.GetValueOrDefault(name) + 1;
            }
        }
    }
    Console.WriteLine($"{lists:N0} list fields ({elements:N0} elements) in {pluginName}, read twice; differing: " +
                      (differing.Count == 0 ? "none" : string.Join(", ", differing.Select(d => $"{d.Key} {d.Value}"))));
    return differing.Count == 0 ? 0 : 1;
}

// Reading every winner of a type through the index, step by step.
static int TimeFetch(string data, string pluginsFile, string type)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    static long Heap() => GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
    var before = Heap();
    var clock = Stopwatch.StartNew();
    var index = snapshot.Index;
    Console.WriteLine($"index: {clock.ElapsedMilliseconds} ms ({index.Count:N0} records), heap +{Heap() - before} MiB");
    before = Heap();
    var signatures = SafePatch.Authoring.Query.RecordTypes.Signatures(type);
    var chains = index.Chains.Where(c => signatures.Contains(c.Signature)).ToList();
    var queries = new SafePatch.Authoring.Query.QueryService(snapshot);
    for (var pass = 1; pass <= 2; pass++)
    {
        clock.Restart();
        var read = chains.Count(c => queries.Winner(c) is not null);
        Console.WriteLine($"pass {pass}: fetched {read:N0} of {chains.Count:N0} {type} winners in {clock.ElapsedMilliseconds} ms, heap +{Heap() - before} MiB since the index");
    }
    clock.Restart();
    var viaGroups = snapshot.Mods.Sum(m => m.EnumerateMajorRecords(SafePatch.Authoring.Query.RecordTypes.Getter(type)).Count());
    Console.WriteLine($"Mutagen enumerating every {type} version: {viaGroups:N0} in {clock.ElapsedMilliseconds} ms");
    return 0;
}

// Every version the index reader decodes must equal Mutagen's own read of it, field by field.
static int CheckDecode(string data, string pluginsFile, int perType)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    var index = snapshot.Index;
    var failures = 0;
    var checkedCount = 0;
    var decodeTime = TimeSpan.Zero;
    foreach (var group in index.Chains.GroupBy(c => c.Signature).OrderBy(g => PluginScanner.Name(g.Key)))
    {
        var getter = SafePatch.Authoring.Query.RecordTypes.GetterOf(group.Key);
        if (getter is null)
        {
            Console.WriteLine($"{PluginScanner.Name(group.Key)}: not modelled by Mutagen ({group.Count()} records)");
            continue;
        }
        // Spread the sample over the type: originals, overrides, and the last records of big plugins.
        var chains = group.ToList();
        var sample = Enumerable.Range(0, Math.Min(perType, chains.Count)).Select(i => chains[(int)((long)i * chains.Count / Math.Min(perType, chains.Count))]);
        var typeFailures = new List<string>();
        foreach (var chain in sample)
        {
            for (var v = 0; v < chain.Versions; v++)
            {
                var clock = Stopwatch.StartNew();
                var decoded = snapshot.Read(chain, v);
                decodeTime += clock.Elapsed;
                var plugin = snapshot.Mods[chain.PluginAt(v)].ModKey;
                var expected = snapshot.LinkCache.ResolveAllContexts(chain.FormKey, getter).FirstOrDefault(c => c.ModKey == plugin)?.Record;
                checkedCount++;
                if (decoded is null || expected is null)
                {
                    if (decoded is null != expected is null) typeFailures.Add($"{chain.FormKey}@{plugin}: decoded {(decoded is null ? "nothing" : "a record")}, Mutagen {(expected is null ? "nothing" : "a record")}");
                    continue;
                }
                var changed = SafePatch.Mutagen.RecordDiff.ChangedFields(expected, decoded);
                if (changed.Count > 0) typeFailures.Add($"{chain.FormKey}@{plugin}: {string.Join(", ", changed)}");
            }
        }
        failures += typeFailures.Count;
        if (typeFailures.Count > 0) Console.WriteLine($"{PluginScanner.Name(group.Key)}: {typeFailures.Count} differ, e.g. {typeFailures[0]}");
    }
    Console.WriteLine($"Checked {checkedCount:N0} versions: {failures} differ. Decoding took {decodeTime.TotalMilliseconds / Math.Max(1, checkedCount) * 1000:F0} µs a record on average.");
    return failures == 0 ? 0 : 1;
}

// One plugin read three ways: Mutagen's full parse (the reference), its overlay, and the index reader.
static int CheckFull(string data, string pluginsFile, string pluginName)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    var index = snapshot.Index;
    var plugin = index.IndexOf(ModKey.FromFileName(pluginName));
    var full = SkyrimMod.CreateFromBinary(new Mutagen.Bethesda.Plugins.ModPath(index.Paths[plugin]), SkyrimRelease.SkyrimSE);
    var reference = full.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
    var overlay = snapshot.Mods[plugin].EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
    var readerDiffers = new Dictionary<string, (int Count, string Example)>();
    var overlayDiffers = new Dictionary<string, (int Count, string Example)>();
    var readerFallbacks = 0;
    var total = 0;
    void Note(Dictionary<string, (int Count, string Example)> into, string type, string example) =>
        into[type] = into.TryGetValue(type, out var n) ? (n.Count + 1, n.Example) : (1, example);
    foreach (var chain in index.Chains)
    {
        var version = chain.VersionOf(plugin);
        if (version < 0 || !reference.TryGetValue(chain.FormKey, out var expected)) continue;
        total++;
        var type = PluginScanner.Name(chain.Signature);
        var decoded = snapshot.Read(chain, version);
        if (decoded is null) { readerFallbacks++; continue; }
        // The full parse has no strings table, so localized text differs from it for both readers: count only
        // fields where one reader disagrees with the full parse and the other does not.
        var byReader = SafePatch.Mutagen.RecordDiff.ChangedFields(expected, decoded).ToHashSet();
        var byOverlay = overlay.TryGetValue(chain.FormKey, out var lazy) ? SafePatch.Mutagen.RecordDiff.ChangedFields(expected, lazy).ToHashSet() : [];
        if (byReader.Except(byOverlay).ToList() is { Count: > 0 } a) Note(readerDiffers, type, $"{chain.FormKey}: {string.Join(", ", a)}");
        if (byOverlay.Except(byReader).ToList() is { Count: > 0 } b) Note(overlayDiffers, type, $"{chain.FormKey}: {string.Join(", ", b)}");
    }
    Console.WriteLine($"{pluginName}: {total:N0} records; the reader returned nothing for {readerFallbacks} (read through the overlay instead).");
    Console.WriteLine("Only the reader differs from the full parse: " + (readerDiffers.Count == 0 ? "never" : string.Join("; ", readerDiffers.Select(d => $"{d.Key} {d.Value.Count} (e.g. {d.Value.Example})"))));
    Console.WriteLine("Only the overlay differs from the full parse: " + (overlayDiffers.Count == 0 ? "never" : string.Join("; ", overlayDiffers.Select(d => $"{d.Key} {d.Value.Count} (e.g. {d.Value.Example})"))));
    return readerDiffers.Count == 0 ? 0 : 1;
}

// Queries as an MCP session runs them: one snapshot, the index built by the first query; each query's first run, then
// the median of nine more.
static int Session(string data, string pluginsFile)
{
    var clock = Stopwatch.StartNew();
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    Console.WriteLine($"open: {clock.ElapsedMilliseconds} ms");
    var queries = new SafePatch.Authoring.Query.QueryService(snapshot);
    (string Name, Func<SafePatch.Authoring.Output.ResultSet> Run)[] steps =
    [
        ("load_order Dawnguard.esm", () => queries.LoadOrder("Dawnguard.esm")),
        ("get_record IronSword", () => queries.GetRecord("IronSword")),
        ("compare_record IronSword", () => queries.CompareRecord("IronSword")),
        ("find_records Static *Rock*", () => queries.FindRecords(new(["Static"], EditorId: "*Rock*"))),
        ("find_records all *IronSword*", () => queries.FindRecords(new(EditorId: "*IronSword*"))),
        ("find_records Npc where Name contains Guard", () => queries.FindRecords(new(["Npc"], Where: "Name contains Guard"))),
        ("find_conflicts Npc", () => queries.FindConflicts(new(["Npc"]))),
        ("references IronSword in", () => queries.References("IronSword", SafePatch.Authoring.Query.LinkDirection.In)),
        ("describe_type", () => queries.DescribeType()),
        ("find_asset longsword.nif", () => queries.FindAsset("meshes/weapons/iron/longsword.nif")),
        ("(open every archive, find the folder)", () =>
        {
            foreach (var bsa in Directory.EnumerateFiles(data, "*.bsa"))
            {
                var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, bsa);
                reader.TryGetFolder(@"meshes\weapons\iron", out _);
            }
            return SafePatch.Authoring.Output.ResultSet.Lines("", []);
        }),
        ("(raw text search longsword.nif)", () => { _ = snapshot.Index.WinnersContainingText("longsword.nif"); return SafePatch.Authoring.Output.ResultSet.Lines("", []); }),
        ("(raw FormID search IronSword)", () => { _ = snapshot.Index.WinnersMentioning(snapshot.Index.FindEditorId("IronSword")!.Value.FormKey); return SafePatch.Authoring.Output.ResultSet.Lines("", []); }),
    ];
    foreach (var (name, run) in steps)
    {
        clock.Restart();
        var first = run();
        var firstTime = clock.ElapsedMilliseconds;
        var again = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            clock.Restart();
            run();
            again.Add(clock.Elapsed.TotalMilliseconds);
        }
        again.Sort();
        Console.WriteLine($"{name,-44} first {firstTime,6} ms, again {again[again.Count / 2],6:F0} ms median ({again[0]:F0}-{again[^1]:F0})  ({first.Rows.Count} rows)");
    }
    return 0;
}

// The conflict queries on a large synthetic load order, with the one-off costs (index, stale check) apart.
static int SessionScale(string data, string pluginsFile)
{
    var clock = Stopwatch.StartNew();
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    Console.WriteLine($"open: {clock.ElapsedMilliseconds} ms");
    clock.Restart();
    _ = snapshot.Index;
    Console.WriteLine($"index: {clock.ElapsedMilliseconds} ms ({string.Join(", ", snapshot.Index.Timings.Select(t => $"{t.Key} {t.Value.TotalMilliseconds:F0}"))})");
    clock.Restart();
    _ = snapshot.StaleReason();
    Console.WriteLine($"stale check: {clock.ElapsedMilliseconds} ms");
    var queries = new SafePatch.Authoring.Query.QueryService(snapshot);
    (string Name, Func<SafePatch.Authoring.Output.ResultSet> Run)[] steps =
    [
        ("find_conflicts LeveledItem", () => queries.FindConflicts(new(["LeveledItem"]))),
        ("find_conflicts group (drill-in)", () => queries.FindConflicts(new(["LeveledItem"], Group: queries.FindConflicts(new(["LeveledItem"])).Rows[0][0]))),
        ("compare_record ScaleList2004", () => queries.CompareRecord("ScaleList2004")),
        ("compare_record ScaleList0", () => queries.CompareRecord("ScaleList0")),
    ];
    foreach (var (name, run) in steps)
    {
        for (var i = 0; i < 3; i++)
        {
            clock.Restart();
            var set = run();
            Console.WriteLine($"{name,-32} run {i}: {clock.ElapsedMilliseconds,6} ms ({set.Rows.Count} rows)");
        }
    }
    return 0;
}

// find_records' EditorID patterns, against the regular expression they replaced, over every record.
static int CheckGlob(string data, string pluginsFile)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    var queries = new SafePatch.Authoring.Query.QueryService(snapshot);
    var editorIds = snapshot.Index.Chains.Select(c => (c.FormKey, c.EditorId)).ToList();
    string[] patterns = ["*sword*", "Iron*", "*Rock*01", "?ron*", "*a*b*c*", "*e*e*e*e*", "*", "*_*_*", "Dun*Ambush*", "*S?o*D", "zzz*", "*ÄÖ*", "a*", "*01", "**ore**"];
    var failures = 0;
    foreach (var pattern in patterns)
    {
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var expected = editorIds.Where(e => e.EditorId is { } id && regex.IsMatch(id)).Select(e => e.FormKey.ToString()).ToList();
        var clock = Stopwatch.StartNew();
        var actual = queries.FindRecords(new(EditorId: pattern)).Rows.Select(r => r[0]!).ToList();
        var same = expected.SequenceEqual(actual);
        if (!same) failures++;
        Console.WriteLine($"{pattern,-14} {expected.Count,8:N0} expected, {actual.Count,8:N0} found, {clock.ElapsedMilliseconds,5} ms {(same ? "" : "DIFFERENT")}");
    }
    return failures == 0 ? 0 : 1;
}

// Every link of every winning record (a parent's own, not its children's) against the link types its type declares.
static int CheckLinks(string data, string pluginsFile)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    var queries = new SafePatch.Authoring.Query.QueryService(snapshot);
    var index = snapshot.Index;
    var chains = index.Chains.ToList();
    var clock = Stopwatch.StartNew();
    var misses = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    var mistyped = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    long links = 0, records = 0;
    Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, chains.Count, 4096), range =>
    {
        for (var i = range.Item1; i < range.Item2; i++)
        {
            var chain = chains[i];
            if (queries.Winner(chain) is not { } record) continue;
            var source = record.Registration.ClassType;
            IMajorRecordGetter own = record;
            if (SafePatch.Mutagen.RecordDiff.ChildFields(source).Count > 0)
            {
                var copy = (IMajorRecord)System.Activator.CreateInstance(source, [record.FormKey, SkyrimRelease.SkyrimSE])!;
                SafePatch.Mutagen.RecordDiff.CopyIn(copy, record);
                own = copy;
            }
            Interlocked.Increment(ref records);
            if (own is Mutagen.Bethesda.Plugins.Assets.IAssetLinkContainerGetter container
                && container.EnumerateAssetLinks(Mutagen.Bethesda.Plugins.Assets.AssetLinkQuery.Listed).Any(a => !a.IsNull)
                && !SafePatch.Mutagen.LinkTypes.MayHoldAssets(source))
                misses.AddOrUpdate($"{source.Name} lists assets", 1, (_, n) => n + 1);
            foreach (var link in own.EnumerateFormLinks())
            {
                if (link.IsNull || index.Find(link.FormKey) is not { } target) continue;
                Interlocked.Increment(ref links);
                var targetClass = SafePatch.Authoring.Query.RecordTypes.ClassOf(target.Signature);
                if (targetClass is null) continue;
                if (!SafePatch.Mutagen.LinkTypes.MayLinkTo(source, targetClass))
                {
                    var targetGetter = Loqui.LoquiRegistration.GetRegister(targetClass).GetterType;
                    var key = $"{source.Name} -> {targetClass.Name} (declared {link.Type.Name})";
                    (link.Type.IsAssignableFrom(targetGetter) ? misses : mistyped).AddOrUpdate(key, 1, (_, n) => n + 1);
                }
            }
        }
    });
    Console.WriteLine($"{records:N0} records, {links:N0} links to records in the load order, {clock.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine($"Links the declared types miss: {(misses.IsEmpty ? "none" : "")}");
    foreach (var (key, count) in misses.OrderByDescending(m => m.Value)) Console.WriteLine($"  {count,8:N0} {key}");
    Console.WriteLine($"Links to a record of a type the link does not declare (not found by a filtered search): {(mistyped.IsEmpty ? "none" : "")}");
    foreach (var (key, count) in mistyped.OrderByDescending(m => m.Value)) Console.WriteLine($"  {count,8:N0} {key}");

    var anyTypes = SafePatch.Authoring.Query.RecordTypes.All.Select(SafePatch.Authoring.Query.RecordTypes.Class)
        .Where(t => !t.IsAbstract && SafePatch.Mutagen.LinkTypes.MayLinkTo(t, typeof(Npc)) && SafePatch.Mutagen.LinkTypes.MayLinkTo(t, typeof(LandscapeTexture)) && SafePatch.Mutagen.LinkTypes.MayLinkTo(t, typeof(Weapon)))
        .Select(t => t.Name).ToList();
    Console.WriteLine($"Types that may link to an Npc, a LandscapeTexture and a Weapon alike ({anyTypes.Count}): {string.Join(", ", anyTypes)}");
    Console.WriteLine("Types without asset links: " + string.Join(", ", SafePatch.Authoring.Query.RecordTypes.All.Where(n => !SafePatch.Mutagen.LinkTypes.MayHoldAssets(SafePatch.Authoring.Query.RecordTypes.Class(n)))));
    foreach (var target in new[] { typeof(Weapon), typeof(Npc), typeof(LandscapeTexture), typeof(Cell) })
    {
        var sources = SafePatch.Authoring.Query.RecordTypes.All.Select(SafePatch.Authoring.Query.RecordTypes.Class).Where(t => !t.IsAbstract && SafePatch.Mutagen.LinkTypes.MayLinkTo(t, target)).ToList();
        Console.WriteLine($"{target.Name}: {sources.Count} of {SafePatch.Authoring.Query.RecordTypes.All.Count} types may link to it; not Landscape: {!sources.Contains(typeof(Landscape))}, not NavigationMesh: {!sources.Contains(typeof(NavigationMesh))}");
    }
    return misses.IsEmpty ? 0 : 1;
}

// How long working out every record type's link targets takes, once.
static int LinkTypesTiming()
{
    var types = SafePatch.Authoring.Query.RecordTypes.All.Select(SafePatch.Authoring.Query.RecordTypes.Class).ToList();
    var clock = Stopwatch.StartNew();
    var count = types.Count(t => SafePatch.Mutagen.LinkTypes.MayLinkTo(t, typeof(Weapon)));
    Console.WriteLine($"{types.Count} types, {count} may link to a weapon: {clock.ElapsedMilliseconds} ms");
    return 0;
}

// One record as Mutagen's full parse and its overlay read it: the fields that differ, with both values.
static int DiffRecord(string data, string pluginName, string[] keys)
{
    var path = new ModPath(Path.Combine(data, pluginName));
    var full = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
    using var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
    var linkCache = full.ToImmutableLinkCache();
    var fullByKey = full.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
    foreach (var key in keys)
    {
        // A FormKey, or a type name: the first record of that type the two reads do not agree on.
        var formKeys = FormKey.TryFactory(key, out var one)
            ? (fullByKey.ContainsKey(one) ? [one] : [])
            : overlay.EnumerateMajorRecords().Where(r => r.GetType().Name == key + "BinaryOverlay" && !fullByKey[r.FormKey].Equals(r)).Select(r => r.FormKey).Take(2).ToList();
        foreach (var formKey in formKeys)
        {
            var a = fullByKey[formKey];
            var b = overlay.EnumerateMajorRecords().First(r => r.FormKey == formKey);
            Console.WriteLine($"{formKey} {a.GetType().Name} {a.EditorID}");
            foreach (var field in SafePatch.Mutagen.RecordDiff.ChangedFields(a, b))
                Compare(field, a.GetType().GetProperty(field)?.GetValue(a), b.GetType().GetProperty(field)?.GetValue(b), 1);
            if (SafePatch.Mutagen.RecordDiff.ChangedFields(a, b).Count == 0) Console.WriteLine("  (equals masks agree; Equals does not)");
        }
    }
    static string Show(object? v) => v switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        Noggog.ReadOnlyMemorySlice<byte> slice => Convert.ToHexString(slice.ToArray()),
        System.Collections.IEnumerable e and not string => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        Enum en => $"{en} ({Convert.ToInt64(en)})",
        _ => v.ToString()!.ReplaceLineEndings(" "),
    };
    static void Compare(string name, object? x, object? y, int depth)
    {
        var pad = new string(' ', depth * 2);
        if (depth < 5 && x is Loqui.ILoquiObject && y is Loqui.ILoquiObject && x.GetType() != y.GetType() || depth < 5 && x is Loqui.ILoquiObject && y is Loqui.ILoquiObject)
        {
            var getter = ((Loqui.ILoquiObject)x!).Registration.GetterType;
            var props = getter.GetInterfaces().Prepend(getter).SelectMany(i => i.GetProperties()).DistinctBy(p => p.Name).Where(p => p.GetIndexParameters().Length == 0);
            var any = false;
            foreach (var prop in props)
            {
                object? px, py;
                try { px = prop.GetValue(x); py = prop.GetValue(y); } catch { continue; }
                if (Equals(px, py) || Show(px) == Show(py) && px is not Loqui.ILoquiObject) continue;
                if (px is Loqui.ILoquiObject && Equals(px, py)) continue;
                any = true;
                Compare(name + "." + prop.Name, px, py, depth + 1);
            }
            if (!any) Console.WriteLine($"{pad}{name}: no property differs; Equals says they do ({x!.GetType().Name} vs {y!.GetType().Name})");
            return;
        }
        if (depth < 5 && x is System.Collections.IEnumerable xs and not string && y is System.Collections.IEnumerable ys and not string)
        {
            var xl = xs.Cast<object?>().ToList();
            var yl = ys.Cast<object?>().ToList();
            if (xl.Count == yl.Count && xl.Any(i => i is Loqui.ILoquiObject))
            {
                for (var i = 0; i < xl.Count; i++)
                {
                    if (!Equals(xl[i], yl[i])) Compare($"{name}[{i}]", xl[i], yl[i], depth + 1);
                }
                return;
            }
        }
        Console.WriteLine($"{pad}{name}: full parse {Show(x)}");
        Console.WriteLine($"{pad}{new string(' ', name.Length)}  overlay    {Show(y)}");
    }
    return 0;
}

// The first records of a type that are unequal to a second full parse of the same bytes, and the properties to blame.
static int SelfDiff(string data, string pluginName, string type)
{
    var path = new ModPath(Path.Combine(data, pluginName));
    var first = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).EnumerateMajorRecords().Where(r => r.GetType().Name == type).ToList();
    var second = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).EnumerateMajorRecords().Where(r => r.GetType().Name == type).ToDictionary(r => r.FormKey);
    foreach (var a in first.Where(a => !a.Equals(second[a.FormKey])).Take(2))
    {
        var b = second[a.FormKey];
        Console.WriteLine($"{a.FormKey} {type} {a.EditorID}");
        void Walk(string name, object? x, object? y, int depth)
        {
            if (x is float f && float.IsNaN(f) || x is double d && double.IsNaN(d)) Console.WriteLine($"  {name}: NaN");
            if (depth < 8 && x is System.Collections.IDictionary xd && y is System.Collections.IDictionary yd)
            {
                foreach (var key in xd.Keys) Walk($"{name}[{key}]", xd[key], yd.Contains(key) ? yd[key] : null, depth + 1);
                return;
            }
            if (Equals(x, y) && x is not Loqui.ILoquiObject) return;
            if (depth < 8 && x is Loqui.ILoquiObject && y is Loqui.ILoquiObject)
            {
                foreach (var prop in x.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(object)))
                {
                    object? px, py;
                    try { px = prop.GetValue(x); py = prop.GetValue(y); } catch { continue; }
                    Walk($"{name}.{prop.Name}", px, py, depth + 1);
                }
                return;
            }
            if (depth < 8 && x is System.Collections.IList xl && y is System.Collections.IList yl && xl.Count == yl.Count)
            {
                for (var i = 0; i < xl.Count; i++) Walk($"{name}[{i}]", xl[i], yl[i], depth + 1);
                return;
            }
            Console.WriteLine($"  {name}: {x?.GetType().Name} {x} / {y}");
        }
        Walk(type, a, b, 0);
    }
    return 0;
}

static int ProbeApi()
{
    static void Show(Type type, Func<System.Reflection.MethodBase, bool> filter)
    {
        foreach (var m in type.GetConstructors().Cast<System.Reflection.MethodBase>()
                     .Concat(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                     .Where(filter))
            Console.WriteLine($"{type.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }
    Show(typeof(Mutagen.Bethesda.Plugins.Binary.Streams.ParsingMeta), m => m is System.Reflection.ConstructorInfo || m.Name.StartsWith("Get"));
    Show(typeof(PlacedObject), m => m.Name.Contains("Binary"));
    foreach (var t in typeof(PlacedObject).Assembly.GetTypes().Where(t => t.Name is "PlacedObjectBinaryOverlay" or "PlacedObjectBinaryCreateTranslation"))
        Show(t, m => m.Name.Contains("Factory") || m.Name.Contains("Create"));
    Show(typeof(Mutagen.Bethesda.Plugins.Masters.MasterReferenceCollection), m => m is System.Reflection.ConstructorInfo || m.Name.StartsWith("From"));
    var core = typeof(Mutagen.Bethesda.Plugins.Binary.Streams.ParsingMeta).Assembly;
    var package = core.GetTypes().First(t => t.Name == "IReadOnlySeparatedMasterPackage");
    foreach (var t in core.GetTypes().Where(t => package.IsAssignableFrom(t) && !t.IsInterface)) Show(t, m => m.IsStatic || m is System.Reflection.ConstructorInfo);
    Show(typeof(Mutagen.Bethesda.Strings.StringsFolderLookupOverlay), m => m.IsStatic || m is System.Reflection.ConstructorInfo);
    Console.WriteLine(typeof(PlacedObject).GetMethod("CreateFromBinary")!.GetParameters()[1].ParameterType.FullName);
    Show(typeof(Mutagen.Bethesda.Plugins.Binary.Streams.ParsingMeta), m => m.Name.StartsWith("set_"));
    return 0;
}

// Record classes whose signature the index cannot map to Mutagen.
static int CheckTypes()
{
    var unmapped = SafePatch.Authoring.Query.RecordTypes.All
        .Where(t => SafePatch.Authoring.Query.RecordTypes.Signatures(t).Count == 0).ToList();
    Console.WriteLine($"{SafePatch.Authoring.Query.RecordTypes.All.Count} classes, {unmapped.Count} unmapped: {string.Join(", ", unmapped)}");
    return unmapped.Count == 0 ? 0 : 1;
}

// Incoming links found by a Mutagen scan of every winner, against the index's raw search.
static int CheckRefs(string data, string pluginsFile, string id)
{
    using var snapshot = LoadOrderSnapshot.Open(new DataFolderSource(data, pluginsFile));
    var clock = Stopwatch.StartNew();
    _ = snapshot.Index;
    Console.WriteLine($"index {clock.ElapsedMilliseconds} ms");
    clock.Restart();
    var raw = snapshot.Index.WinnersMentioning(snapshot.Index.FindEditorId(id)!.Value.FormKey);
    Console.WriteLine($"raw search {clock.ElapsedMilliseconds} ms: {raw.Count} candidates, by type {string.Join(", ", raw.GroupBy(c => PluginScanner.Name(c.Signature)).Select(g => $"{g.Key} {g.Count()}"))}");
    clock.Restart();
    var target = new SafePatch.Authoring.Query.QueryService(snapshot).References(id, SafePatch.Authoring.Query.LinkDirection.In);
    Console.WriteLine($"references (raw search again, then confirm) {clock.ElapsedMilliseconds} ms");
    var viaIndex = target.Rows.Select(r => r[1]!).ToHashSet();
    var key = FormKey.Factory(target.Title.Split(' ')[2]);
    var viaMutagen = new HashSet<string>();
    var seen = new HashSet<FormKey>();
    foreach (var mod in snapshot.Mods.Reverse())
    {
        foreach (var record in mod.EnumerateMajorRecords())
        {
            if (!seen.Add(record.FormKey)) continue;
            if (record.EnumerateFormLinks().Any(l => l.FormKey == key)) viaMutagen.Add(record.FormKey.ToString());
        }
    }
    Console.WriteLine($"index {viaIndex.Count}, Mutagen {viaMutagen.Count}");
    string Kind(string formKey) => snapshot.Index.Find(FormKey.Factory(formKey)) is { } c ? PluginScanner.Name(c.Signature) : "?";
    Console.WriteLine("missing by type: " + string.Join(", ", viaMutagen.Except(viaIndex).GroupBy(Kind).Select(g => $"{g.Key} {g.Count()}")));
    Console.WriteLine("extra by type: " + string.Join(", ", viaIndex.Except(viaMutagen).GroupBy(Kind).Select(g => $"{g.Key} {g.Count()}")));
    foreach (var missing in viaMutagen.Except(viaIndex).Take(20))
    {
        var chain = snapshot.Index.Find(FormKey.Factory(missing));
        Console.WriteLine($"  missing {missing}: index {(chain is null ? "has no chain" : $"{PluginScanner.Name(chain.Value.Signature)} winner {snapshot.Index.Plugins[chain.Value.Winner]}")}");
    }
    foreach (var extra in viaIndex.Except(viaMutagen).Take(20)) Console.WriteLine($"  extra {extra}");
    return viaIndex.SetEquals(viaMutagen) ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("SafePatch.Benchmarks write-scale <folder> [plugins] [records] | bench <Data folder> <plugins.txt> [runs]");
    return 2;
}

static int WriteScale(string folder, string[] rest)
{
    var plugins = rest.Length > 0 ? int.Parse(rest[0]) : 2000;
    var records = rest.Length > 1 ? int.Parse(rest[1]) : 1;
    // Like a real list of thousands of plugins, the overriding plugins are ESL-flagged (the game loads at most 254 full ones).
    var light = rest.Contains("--light");
    var fixture = new ScaleFixture(plugins, Lists: 2000 * records, OverridesPerList: 6, EntriesPerList: 8, References: 5000 * records, LargeLists: 5);
    var data = Path.Combine(folder, "Data");
    Directory.CreateDirectory(data);
    var clock = Stopwatch.StartNew();
    var mods = fixture.Build();
    var skyrim = new SkyrimMod(ModKey.FromFileName("Skyrim.esm"), SkyrimRelease.SkyrimSE);
    skyrim.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
    skyrim.WriteToBinary(Path.Combine(data, "Skyrim.esm"));
    foreach (var mod in mods)
    {
        if (light && mod.ModKey.FileName != ScaleFixture.BaseName) mod.ModHeader.Flags |= (SkyrimModHeader.HeaderFlag)0x200;
        mod.WriteToBinary(Path.Combine(data, mod.ModKey.FileName));
    }
    File.WriteAllLines(Path.Combine(folder, "plugins.txt"), mods.Select(m => $"*{m.ModKey.FileName}"));
    Console.WriteLine($"Wrote {fixture} ({mods.Count + 1} plugins) in {clock.Elapsed.TotalSeconds:F1} s.");
    return 0;
}

static int Bench(string data, string pluginsFile, int runs)
{
    var source = new DataFolderSource(data, pluginsFile);
    // The load order both sides read: the plugins Mutagen lists, in order, with their files.
    List<(ModKey Key, string Path)> order;
    using (var probe = LoadOrderSnapshot.Open(source))
        order = [.. probe.Mods.Select(m => (m.ModKey, probe.View.TopFiles[m.ModKey.FileName].RealPath))];
    var megabytes = order.Sum(o => new FileInfo(o.Path).Length) / (1024.0 * 1024);
    Console.WriteLine($"{order.Count} plugins, {megabytes:F0} MiB, {Environment.ProcessorCount} logical processors, {runs} runs each (first run in brackets).");

    var statics = new HashSet<uint> { PluginScanner.Signature("STAT"), PluginScanner.Signature("CELL"), PluginScanner.Signature("REFR"), PluginScanner.Signature("WRLD") };
    var results = new List<(string Name, Func<Index> Run)>
    {
        ("Mutagen: open the load order", () => { using var s = LoadOrderSnapshot.Open(source); return new Index(0, 0, 0); }),
        ("Mutagen: index every record, sequential", () => MutagenIndex(source, parallel: false)),
        ("Mutagen: index every record, parallel", () => MutagenIndex(source, parallel: true)),
        ("Mutagen: statics and references, parallel", () => MutagenStatics(source)),
        ("Scanner: index every record, sequential", () => ScannerIndex(order, parallel: false, null)),
        ("Scanner: index every record, parallel", () => ScannerIndex(order, parallel: true, null)),
        ("Scanner: statics and references, parallel", () => ScannerIndex(order, parallel: true, statics)),
        ("LoadOrderIndex (what queries use): open, then build", () =>
        {
            using var snapshot = LoadOrderSnapshot.Open(source);
            var built = snapshot.Index;
            Console.Write($"    ({string.Join(", ", built.Timings.Select(t => $"{t.Key} {t.Value.TotalMilliseconds:F0} ms"))})");
            return new Index(built.Chains.Sum(c => (long)c.Versions), built.Count, 0);
        }),
    };

    Index? mutagen = null, scanner = null;
    foreach (var (name, run) in results)
    {
        var times = new List<double>();
        Index result = default;
        for (var i = 0; i < runs; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var clock = Stopwatch.StartNew();
            result = run();
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        var peak = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        Console.WriteLine($"{name,-44} best {times.Min(),8:F0} ms  [{times[0],8:F0}]  {result}  (peak working set so far {peak} MiB)");
        if (name == "Mutagen: index every record, parallel") mutagen = result;
        if (name == "Scanner: index every record, parallel") scanner = result;
    }
    Console.WriteLine(mutagen == scanner ? "Mutagen and the scanner found the same records, chains and EditorIDs." : $"MISMATCH: Mutagen {mutagen}, scanner {scanner}");
    return mutagen == scanner ? 0 : 1;
}

// Every record version: its FormKey and EditorID, gathered into override chains, as an index needs.
static Index MutagenIndex(LoadOrderSource source, bool parallel)
{
    using var snapshot = LoadOrderSnapshot.Open(source);
    var perMod = new (FormKey Key, string? EditorId)[snapshot.Mods.Count][];
    void One(int i) => perMod[i] = [.. snapshot.Mods[i].EnumerateMajorRecords().Select(r => (r.FormKey, r.EditorID))];
    if (parallel) Parallel.For(0, snapshot.Mods.Count, One);
    else for (var i = 0; i < snapshot.Mods.Count; i++) One(i);
    return Index.Of(perMod);
}

// What SARP's static reader extracts: statics with their models, and placed references with base and position.
static Index MutagenStatics(LoadOrderSource source)
{
    using var snapshot = LoadOrderSnapshot.Open(source);
    long models = 0, references = 0;
    Parallel.For(0, snapshot.Mods.Count, i =>
    {
        var mod = snapshot.Mods[i];
        long m = 0, r = 0;
        foreach (var stat in mod.Statics) if (stat.Model?.File is not null) m++;
        foreach (var placed in mod.EnumerateMajorRecords<IPlacedObjectGetter>())
            if (!placed.Base.IsNull && placed.Placement is not null) r++;
        Interlocked.Add(ref models, m);
        Interlocked.Add(ref references, r);
    });
    return new Index(models, references, 0);
}

static Index ScannerIndex(List<(ModKey Key, string Path)> order, bool parallel, IReadOnlySet<uint>? wanted)
{
    var scans = new ScannedPlugin[order.Count];
    void One(int i) => scans[i] = PluginScanner.Scan(order[i].Path, wanted);
    if (parallel) Parallel.For(0, order.Count, One);
    else for (var i = 0; i < order.Count; i++) One(i);

    var perMod = new (FormKey Key, string? EditorId)[order.Count][];
    for (var i = 0; i < order.Count; i++)
    {
        var self = order[i].Key;
        var masters = scans[i].Masters.Select(m => ModKey.FromFileName(m)).ToArray();
        perMod[i] = [.. scans[i].Records.Select(r =>
        {
            var index = (int)(r.FormId >> 24);
            return (new FormKey(index < masters.Length ? masters[index] : self, r.FormId & 0xFFFFFF), r.EditorId);
        })];
    }
    return Index.Of(perMod);
}

/// <summary>What an index holds, as counts and a checksum, to compare the two readers.</summary>
internal readonly record struct Index(long Versions, long Chains, long Checksum)
{
    public static Index Of((FormKey Key, string? EditorId)[][] perMod)
    {
        var chains = new Dictionary<FormKey, int>();
        long versions = 0, checksum = 0;
        foreach (var records in perMod)
        {
            foreach (var (key, editorId) in records)
            {
                versions++;
                chains[key] = chains.GetValueOrDefault(key) + 1;
                checksum += (key.GetHashCode() ^ StringComparer.Ordinal.GetHashCode(editorId ?? "")) & 0xFFFF;
            }
        }
        return new Index(versions, chains.Count, checksum);
    }

    public override string ToString() => Checksum == 0 && Chains == 0 && Versions == 0 ? "" : $"{Versions:N0} versions, {Chains:N0} chains";
}
