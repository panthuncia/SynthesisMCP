using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Query;

namespace SafePatch.Authoring.Index;

/// <summary>A record's versions in the load order: a view into <see cref="LoadOrderIndex"/>, cheap to copy.</summary>
public readonly struct RecordChain : IEquatable<RecordChain>
{
    private readonly LoadOrderIndex _index;
    private readonly int _id;

    internal RecordChain(LoadOrderIndex index, int id)
    {
        _index = index;
        _id = id;
    }

    public FormKey FormKey => _index.KeyOf(_id);

    /// <summary>The record type, as its four-character signature.</summary>
    public uint Signature => _index.SignatureOf(_id);

    public int Versions => _index.VersionCount(_id);

    /// <summary>The plugin (an index into <see cref="LoadOrderIndex.Plugins"/>) holding the <paramref name="version"/>th version, lowest priority first.</summary>
    public int PluginAt(int version) => _index.VersionPlugin(_id, version);

    /// <summary>
    /// The record the <paramref name="version"/>th version is nested in (a placed reference's cell, a cell's
    /// worldspace, a response's topic), in the same plugin, or null for a top-level record.
    /// </summary>
    public RecordChain? ParentAt(int version) => _index.VersionParent(_id, version) is var parent and >= 0 ? new RecordChain(_index, parent) : null;

    /// <summary>The plugins holding a version, lowest priority first.</summary>
    public int[] Plugins => [.. Enumerable.Range(0, Versions).Select(PluginAt)];

    public int Winner => PluginAt(Versions - 1);

    public bool Contains(int plugin) => VersionOf(plugin) >= 0;

    /// <summary>Which version a plugin holds, or -1.</summary>
    public int VersionOf(int plugin)
    {
        for (var v = 0; v < Versions; v++)
        {
            if (PluginAt(v) == plugin) return v;
        }
        return -1;
    }

    /// <summary>The winning version's EditorID, or the latest one set if the winner has none (a deleted winner).</summary>
    public string? EditorId => _index.EditorIdOf(_id);

    /// <summary>Whether <see cref="EditorId"/> matches a pattern, without reading it into a string.</summary>
    internal bool EditorIdMatches(EditorIdGlob glob) => _index.EditorIdMatches(_id, glob);

    public bool WinnerDeleted => _index.FlagsOf(_id).HasFlag(ChainFlags.WinnerDeleted);

    public bool AnyDeleted => _index.FlagsOf(_id).HasFlag(ChainFlags.AnyDeleted);

    public bool Equals(RecordChain other) => ReferenceEquals(_index, other._index) && _id == other._id;
    public override bool Equals(object? obj) => obj is RecordChain other && Equals(other);
    public override int GetHashCode() => _id;
    public static bool operator ==(RecordChain left, RecordChain right) => left.Equals(right);
    public static bool operator !=(RecordChain left, RecordChain right) => !left.Equals(right);
    public override string ToString() => $"{FormKey} {PluginScanner.Name(Signature)} {EditorId}";
}

[Flags]
internal enum ChainFlags : byte
{
    None = 0,
    WinnerDeleted = 1,
    AnyDeleted = 2,
}

/// <summary>Per plugin and record type, how many records it defines and how many it overrides.</summary>
public readonly record struct TypeCount(uint Signature, int New, int Overrides);

/// <summary>
/// Every record in a load order, read through Mutagen's overlays: FormKeys, types, EditorIDs, and which plugins hold a
/// version of each. Queries use it to pick records by type, plugin, EditorID or version count and to find candidates
/// for conflicts and references; <see cref="LoadOrderSnapshot.Read"/> then reads just the versions a result shows.
/// Building it reads only record headers and EditorIDs (Mutagen defers the rest of a record until a field is read, and
/// peeks at the EditorID), over every plugin's record batches in parallel. Stored as flat arrays, no object per record.
/// </summary>
public sealed class LoadOrderIndex
{
    // Per record, by id (the order its first version appears in the load order).
    private readonly ulong[] _keys;            // (ModKey table index << 32) | FormID
    private readonly uint[] _signatures;
    private readonly int[] _versionStart;      // into _versionPlugins; one extra entry at the end
    private readonly int[] _editorIds;         // start in _editorIdPool, or -1
    private readonly ChainFlags[] _flags;
    private readonly int[] _versionPlugins;
    private readonly int[] _versionParents;    // the id of the record each version is nested in, or -1
    private readonly byte[] _editorIdPool;     // length (ushort) then Latin-1 bytes
    private readonly ModKey[] _modKeys;
    private readonly Dictionary<ModKey, int> _modKeyIds;

    // Lookups: sorted keys, and EditorIDs sorted by case-insensitive hash.
    private readonly ulong[] _sortedKeys;
    private readonly int[] _sortedKeyIds;
    private readonly ulong[] _editorIdHashes;  // (hash << 32) | id

    private LoadOrderIndex(Builder built, IReadOnlyList<ModKey> plugins, IReadOnlyList<string> paths, IReadOnlyList<IReadOnlyList<ModKey>> masters,
        IReadOnlyList<IReadOnlyList<TypeCount>> counts, IReadOnlyList<(float Header, ushort Form)> versions, TimeSpan elapsed)
    {
        Plugins = plugins;
        HeaderVersions = versions;
        Paths = paths;
        Masters = masters;
        TypeCounts = counts;
        _modKeys = [.. built.ModKeys];
        _modKeyIds = built.ModKeyIds;
        _keys = [.. built.Keys];
        _signatures = [.. built.Signatures];
        _editorIds = [.. built.EditorIds];
        _flags = [.. built.Flags];
        _editorIdPool = [.. built.EditorIdPool];

        // Versions arrive plugin by plugin; a stable counting sort makes each record's versions contiguous, in load order.
        var count = _keys.Length;
        _versionStart = new int[count + 1];
        foreach (var (id, _, _) in built.Versions) _versionStart[id + 1]++;
        for (var i = 0; i < count; i++) _versionStart[i + 1] += _versionStart[i];
        _versionPlugins = new int[built.Versions.Count];
        _versionParents = new int[built.Versions.Count];
        var next = _versionStart[..count];
        foreach (var (id, plugin, parent) in built.Versions)
        {
            var slot = next[id]++;
            _versionPlugins[slot] = plugin;
            _versionParents[slot] = parent;
        }

        _sortedKeys = (ulong[])_keys.Clone();
        _sortedKeyIds = [.. Enumerable.Range(0, count)];
        Array.Sort(_sortedKeys, _sortedKeyIds);
        _editorIdHashes = [.. Enumerable.Range(0, count).Where(id => _editorIds[id] >= 0).Select(id => ((ulong)EditorIdHash(EditorIdBytes(id)) << 32) | (uint)id)];
        Array.Sort(_editorIdHashes);
        Elapsed = elapsed;
    }

    /// <summary>The plugins, in load order.</summary>
    public IReadOnlyList<ModKey> Plugins { get; }

    /// <summary>Each plugin's file.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Each plugin's masters, as its header lists them.</summary>
    public IReadOnlyList<IReadOnlyList<ModKey>> Masters { get; }

    /// <summary>Each plugin's header version and form version, which decide how some fields are read.</summary>
    public IReadOnlyList<(float Header, ushort Form)> HeaderVersions { get; }

    /// <summary>Each plugin's records by type, sorted by signature name.</summary>
    public IReadOnlyList<IReadOnlyList<TypeCount>> TypeCounts { get; }

    /// <summary>How long building the index took.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>How long each phase of the build took: read, merge, lookups.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; private set; } = new Dictionary<string, TimeSpan>();

    public int Count => _keys.Length;

    /// <summary>Every record, in the order its first version appears in the load order.</summary>
    public IEnumerable<RecordChain> Chains
    {
        get
        {
            for (var id = 0; id < _keys.Length; id++) yield return new RecordChain(this, id);
        }
    }

    /// <summary><see cref="Chains"/> for a parallel filter, in order. What is tested per chain should be cheap.</summary>
    public ParallelQuery<RecordChain> ParallelChains() => ParallelEnumerable.Range(0, _keys.Length).AsOrdered().Select(id => new RecordChain(this, id));

    public RecordChain? Find(FormKey formKey)
    {
        if (!_modKeyIds.TryGetValue(formKey.ModKey, out var mod)) return null;
        var at = Array.BinarySearch(_sortedKeys, ((ulong)mod << 32) | formKey.ID);
        return at < 0 ? null : new RecordChain(this, _sortedKeyIds[at]);
    }

    /// <summary>A record by its (winning) EditorID, case-insensitively.</summary>
    public RecordChain? FindEditorId(string editorId)
    {
        var wanted = Encoding.Latin1.GetBytes(editorId);
        var hash = (ulong)EditorIdHash(wanted) << 32;
        var at = Array.BinarySearch(_editorIdHashes, hash);
        if (at < 0) at = ~at;
        for (; at < _editorIdHashes.Length && (_editorIdHashes[at] & 0xFFFFFFFF00000000) == hash; at++)
        {
            var id = (int)(_editorIdHashes[at] & 0xFFFFFFFF);
            if (Ascii.EqualsIgnoreCase(EditorIdBytes(id), wanted)) return new RecordChain(this, id);
        }
        return null;
    }

    public int IndexOf(ModKey plugin)
    {
        for (var i = 0; i < Plugins.Count; i++)
        {
            if (Plugins[i] == plugin) return i;
        }
        return -1;
    }

    internal FormKey KeyOf(int id) => new(_modKeys[(int)(_keys[id] >> 32)], (uint)_keys[id]);
    internal uint SignatureOf(int id) => _signatures[id];
    internal int VersionCount(int id) => _versionStart[id + 1] - _versionStart[id];
    internal int VersionPlugin(int id, int version) => _versionPlugins[_versionStart[id] + version];
    internal int VersionParent(int id, int version) => _versionParents[_versionStart[id] + version];
    internal ChainFlags FlagsOf(int id) => _flags[id];
    internal bool EditorIdMatches(int id, EditorIdGlob glob) => _editorIds[id] >= 0 && glob.IsMatch(EditorIdBytes(id));
    internal string? EditorIdOf(int id) => _editorIds[id] < 0 ? null : Encoding.Latin1.GetString(EditorIdBytes(id));

    private ReadOnlySpan<byte> EditorIdBytes(int id)
    {
        var start = _editorIds[id];
        var length = _editorIdPool[start] | (_editorIdPool[start + 1] << 8);
        return _editorIdPool.AsSpan(start + 2, length);
    }

    /// <summary>FNV-1a over the ASCII-lowercased bytes.</summary>
    private static uint EditorIdHash(ReadOnlySpan<byte> bytes)
    {
        var hash = 2166136261u;
        foreach (var b in bytes) hash = (hash ^ (uint)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b)) * 16777619u;
        return hash;
    }

    /// <summary>What the index keeps of one record version, and the record it is nested in.</summary>
    private readonly record struct Entry(FormKey FormKey, uint Signature, string? EditorId, bool IsDeleted, FormKey? Parent);

    /// <summary>The top-level groups whose records are read as they are (worldspaces and cells are walked instead).</summary>
    private static readonly System.Reflection.PropertyInfo[] Groups = [.. typeof(ISkyrimModGetter).GetProperties()
        .Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType) && p.Name != nameof(ISkyrimModGetter.Worldspaces))];

    /// <summary>
    /// A plugin's records in batches that can be read on different threads (as Mutagen's
    /// <c>EnumerateMajorRecordBatches</c>), each with the record it is nested in: each top-level group, each worldspace
    /// with its persistent cell, and each block of interior or exterior cells. A parent comes before its children.
    /// </summary>
    private static IEnumerable<IEnumerable<(IMajorRecordGetter Record, IMajorRecordGetter? Parent)>> Batches(ISkyrimModGetter mod)
    {
        static IEnumerable<(IMajorRecordGetter, IMajorRecordGetter?)> WithChildren(IMajorRecordGetter record, IMajorRecordGetter? parent) =>
            record is IMajorRecordGetterEnumerable children
                ? children.EnumerateMajorRecords().Select(child => (child, (IMajorRecordGetter?)record)).Prepend((record, parent))
                : [(record, parent)];

        foreach (var property in Groups)
        {
            yield return ((IGroupGetter)property.GetValue(mod)!).Records.SelectMany(r => WithChildren(r, null));
        }
        foreach (var block in mod.Cells.Records)
        {
            foreach (var subBlock in block.SubBlocks) yield return subBlock.Cells.SelectMany(c => WithChildren(c, null));
        }
        foreach (var world in mod.Worldspaces)
        {
            yield return world.TopCell is { } top ? WithChildren(top, world).Prepend((world, null)) : [(world, null)];
            foreach (var block in world.SubCells)
            {
                foreach (var subBlock in block.Items) yield return subBlock.Items.SelectMany(c => WithChildren(c, world));
            }
        }
    }

    /// <summary>
    /// Reads every plugin's records (all plugins' batches in parallel) and builds the index, in load order.
    /// <paramref name="paths"/> are the plugins' files, which the raw text searches read.
    /// </summary>
    public static LoadOrderIndex Build(IReadOnlyList<ISkyrimModGetter> mods, IReadOnlyList<string> paths)
    {
        var clock = Stopwatch.StartNew();
        var batches = mods.SelectMany((mod, p) => Batches(mod).Select(batch => (Plugin: p, Records: batch))).ToList();
        var read = new Entry[batches.Count][];
        Parallel.For(0, batches.Count, b => read[b] = [.. batches[b].Records.Select(r =>
            new Entry(r.Record.FormKey, RecordTypes.SignatureOf(r.Record), r.Record.EditorID, r.Record.IsDeleted, r.Parent?.FormKey))]);
        var readTime = clock.Elapsed;

        var built = new Builder(read.Sum(r => r.Length));
        var counts = new Dictionary<uint, (int New, int Overrides)>[mods.Count];
        for (var p = 0; p < mods.Count; p++) counts[p] = [];
        for (var b = 0; b < batches.Count; b++)
        {
            var p = batches[b].Plugin;
            var self = mods[p].ModKey;
            foreach (var entry in read[b])
            {
                var count = counts[p].GetValueOrDefault(entry.Signature);
                counts[p][entry.Signature] = entry.FormKey.ModKey == self ? (count.New + 1, count.Overrides) : (count.New, count.Overrides + 1);
                built.Add(entry, p);
            }
            read[b] = null!; // let each batch go as soon as it is merged
        }
        var merged = clock.Elapsed;
        var index = new LoadOrderIndex(built, [.. mods.Select(m => m.ModKey)], paths,
            [.. mods.Select(m => (IReadOnlyList<ModKey>)[.. m.ModHeader.MasterReferences.Select(r => r.Master)])],
            [.. counts.Select(c => (IReadOnlyList<TypeCount>)[.. c.Select(t => new TypeCount(t.Key, t.Value.New, t.Value.Overrides)).OrderBy(t => PluginScanner.Name(t.Signature), StringComparer.Ordinal)])],
            [.. mods.Select(m => (m.ModHeader.Stats.Version, m.ModHeader.FormVersion))], clock.Elapsed);
        index.Timings = new Dictionary<string, TimeSpan> { ["read"] = readTime, ["merge"] = merged - readTime, ["lookups"] = clock.Elapsed - merged };
        index.Elapsed = clock.Elapsed;
        return index;
    }

    private sealed class Builder(int capacity)
    {
        public readonly List<ModKey> ModKeys = [];
        public readonly Dictionary<ModKey, int> ModKeyIds = [];
        public readonly List<ulong> Keys = new(capacity);
        public readonly List<uint> Signatures = new(capacity);
        public readonly List<int> EditorIds = new(capacity);
        public readonly List<ChainFlags> Flags = new(capacity);
        public readonly List<byte> EditorIdPool = [];
        public readonly List<(int Id, int Plugin, int Parent)> Versions = new(capacity);
        private readonly Dictionary<ulong, int> _ids = new(capacity);
        private readonly Dictionary<string, int> _editorIdStarts = new(StringComparer.Ordinal);
        private int _lastPlugin = -1;
        private readonly HashSet<int> _inPlugin = [];

        public void Add(Entry record, int plugin)
        {
            var formKey = record.FormKey;
            if (plugin != _lastPlugin)
            {
                _inPlugin.Clear();
                _lastPlugin = plugin;
            }
            var key = Key(formKey);
            var deleted = record.IsDeleted ? ChainFlags.WinnerDeleted | ChainFlags.AnyDeleted : ChainFlags.None;
            if (_ids.TryGetValue(key, out var id))
            {
                if (!_inPlugin.Add(id)) return; // a plugin holds one version of a record
                if (record.EditorId is { } editorId) EditorIds[id] = EditorIdStart(editorId);
                Flags[id] = (Flags[id] & ChainFlags.AnyDeleted) | deleted;
            }
            else
            {
                id = Keys.Count;
                _ids[key] = id;
                _inPlugin.Add(id);
                Keys.Add(key);
                Signatures.Add(record.Signature);
                EditorIds.Add(record.EditorId is { } editorId ? EditorIdStart(editorId) : -1);
                Flags.Add(deleted);
            }
            Versions.Add((id, plugin, record.Parent is { } parent ? _ids[Key(parent)] : -1));
        }

        /// <summary>A FormKey as the index keys it; its plugin is added to the table on first sight.</summary>
        private ulong Key(FormKey formKey)
        {
            if (!ModKeyIds.TryGetValue(formKey.ModKey, out var mod))
            {
                mod = ModKeys.Count;
                ModKeys.Add(formKey.ModKey);
                ModKeyIds[formKey.ModKey] = mod;
            }
            return ((ulong)mod << 32) | formKey.ID;
        }

        /// <summary>Overrides repeat their master's EditorID: each distinct one is stored once.</summary>
        private int EditorIdStart(string editorId)
        {
            if (_editorIdStarts.TryGetValue(editorId, out var start)) return start;
            var bytes = Encoding.Latin1.GetBytes(editorId);
            var length = Math.Min(bytes.Length, ushort.MaxValue);
            start = EditorIdPool.Count;
            EditorIdPool.Add((byte)length);
            EditorIdPool.Add((byte)(length >> 8));
            EditorIdPool.AddRange(bytes.AsSpan(0, length));
            _editorIdStarts[editorId] = start;
            return start;
        }
    }

    /// <summary>
    /// The winning versions that contain <paramref name="target"/>'s FormID as their plugin stores it: a fast, raw
    /// superset of the records linking to it (a coincidental match is possible, so callers confirm with Mutagen).
    /// Plugins that do not master the target's plugin cannot link to it and are not read.
    /// </summary>
    public IReadOnlyList<RecordChain> WinnersMentioning(FormKey target, IReadOnlySet<uint>? signatures = null) =>
        WinnersMatching(p =>
        {
            var masterIndex = Plugins[p] == target.ModKey ? Masters[p].Count : IndexOfMaster(Masters[p], target.ModKey);
            return masterIndex < 0 ? null : PluginScanner.Containing(((uint)masterIndex << 24) | target.ID);
        }, signatures).Where(c => c.FormKey != target).ToList();

    /// <summary>The winning versions whose data contains some text in any case, e.g. a file name: a raw superset for callers to confirm.</summary>
    public IReadOnlyList<RecordChain> WinnersContainingText(string text, IReadOnlySet<uint>? signatures = null) =>
        WinnersMatching(_ => PluginScanner.ContainingText(text), signatures);

    /// <summary>Reads the plugins again (in parallel) for the winning versions a per-plugin matcher accepts; a null matcher skips the plugin.</summary>
    private IReadOnlyList<RecordChain> WinnersMatching(Func<int, PluginScanner.DataMatcher?> matcherFor, IReadOnlySet<uint>? signatures)
    {
        var found = new ConcurrentBag<RecordChain>();
        Parallel.For(0, Plugins.Count, p =>
        {
            if (matcherFor(p) is not { } matcher) return;
            foreach (var formId in PluginScanner.RecordsMatching(Paths[p], matcher, signatures))
            {
                if (Find(FormKeyOf(formId, Masters[p], Plugins[p])) is { } chain && chain.Winner == p) found.Add(chain);
            }
        });
        return [.. found.OrderBy(c => c.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FormKey.ID)];
    }

    /// <summary>A stored FormID's FormKey: its top byte indexes the plugin's masters, or means the plugin itself.</summary>
    public static FormKey FormKeyOf(uint formId, IReadOnlyList<ModKey> masters, ModKey self)
    {
        var index = (int)(formId >> 24);
        return new FormKey(index < masters.Count ? masters[index] : self, formId & 0xFFFFFF);
    }

    private static int IndexOfMaster(IReadOnlyList<ModKey> masters, ModKey plugin)
    {
        for (var i = 0; i < masters.Count; i++)
        {
            if (masters[i] == plugin) return i;
        }
        return -1;
    }
}
