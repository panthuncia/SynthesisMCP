using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Authoring.Index;
using SafePatch.Authoring.Sources;
using SafePatch.Host;

namespace SafePatch.Authoring;

/// <summary>
/// A load order read the way Synthesis reads it: plugins.txt plus the game's implicit masters, from a Data folder
/// that may be layered (an MO2 profile). Plugins are opened read-only as overlays, through a file system that
/// never stops a mod manager from deleting or renaming them. Nothing is ever written.
/// </summary>
public sealed class LoadOrderSnapshot : IDisposable
{
    private static int _generations;
    private readonly IReadOnlyDictionary<string, DateTime> _stamps;
    private readonly Lazy<LoadOrderIndex> _index;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Plugin, Type Getter), IGroupGetter?> _groups = new();

    private LoadOrderSnapshot(LoadOrderSource source, ResolvedSource resolved, ILoadOrder<IModListing<ISkyrimModGetter>> loadOrder, DataViewFileSystem fileSystem)
    {
        Source = source;
        Resolved = resolved;
        LoadOrder = loadOrder;
        Mods = [.. loadOrder.ListedOrder.Where(l => l.Mod is not null).Select(l => l.Mod!)];
        LinkCache = loadOrder.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        Generation = Interlocked.Increment(ref _generations);
        LoadedAt = DateTime.UtcNow;
        _stamps = Stamps(resolved, Mods.Select(m => m.ModKey.FileName.String));
        _index = new(() => LoadOrderIndex.Build(Mods, [.. Mods.Select(m => View.TopFiles[m.ModKey.FileName.String].RealPath)]));
    }

    public LoadOrderSource Source { get; }
    public ResolvedSource Resolved { get; }
    public GameRelease Release => Resolved.Release;
    public DataView View => Resolved.View;

    /// <summary>The Data folder's path as the game names it.</summary>
    public string DataFolder => View.DataFolder;

    public string PluginsFile => Resolved.PluginsFile;

    /// <summary>The game INI the source chose, <c>""</c> for none, or null for Synthesis's lookup.</summary>
    public string? GameIni => Resolved.GameIni;

    public ILoadOrder<IModListing<ISkyrimModGetter>> LoadOrder { get; }

    /// <summary>The plugins that are present, lowest priority first.</summary>
    public IReadOnlyList<ISkyrimModGetter> Mods { get; }

    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache { get; }

    /// <summary>
    /// Every record's FormKey, type, EditorID and versions, read through the plugins' overlays (built on first use, in
    /// parallel). Its plugin indexes are positions in <see cref="Mods"/>.
    /// </summary>
    public LoadOrderIndex Index => _index.Value;

    /// <summary>Whether <see cref="Index"/> has been built yet.</summary>
    public bool IsIndexed => _index.IsValueCreated;

    /// <summary>
    /// One version of a record, read through its plugin's overlay: from the plugin's group for its type, or, for a
    /// record in a cell or worldspace, through the link cache. Every version is read the same way, so versions compare.
    /// </summary>
    /// <param name="version">Which version: 0 is the original.</param>
    public IMajorRecordGetter? Read(RecordChain chain, int version)
    {
        if (Query.RecordTypes.GetterOf(chain.Signature) is not { } getter) return null;
        var mod = Mods[chain.PluginAt(version)];
        // Looked up once per plugin and type; the overlay keeps the group itself.
        var group = _groups.GetOrAdd((chain.PluginAt(version), getter), key => Mods[key.Plugin].TryGetTopLevelGroup(key.Getter));
        if (group is not null) return group.ContainsKey(chain.FormKey) ? group[chain.FormKey] : null;
        // Cells live in blocks rather than a group of their own.
        return LinkCache.ResolveAllContexts(chain.FormKey, getter).FirstOrDefault(c => c.ModKey == mod.ModKey)?.Record;
    }

    /// <summary>Unique to this snapshot: result handles from another snapshot are refused.</summary>
    public int Generation { get; }

    public DateTime LoadedAt { get; }

    public static LoadOrderSnapshot Open(string dataFolder, string pluginsFile, GameRelease release = GameRelease.SkyrimSE) =>
        Open(new DataFolderSource(dataFolder, pluginsFile, release));

    public static LoadOrderSnapshot Open(LoadOrderSource source)
    {
        var resolved = source.Resolve();
        var release = resolved.Release;
        if (release.ToCategory() != GameCategory.Skyrim) throw new SafePatchException($"SafePatch supports Skyrim releases only, not {release}.");

        var data = resolved.View.DataFolder;
        var creationClub = Path.Combine(Path.GetDirectoryName(data)!, "Skyrim.ccc");
        string[] extra = File.Exists(creationClub) ? [resolved.PluginsFile, creationClub] : [resolved.PluginsFile];
        var fileSystem = new DataViewFileSystem(resolved.View, extra);

        var listings = global::Mutagen.Bethesda.Plugins.Order.LoadOrder.GetLoadOrderListings(
            release, resolved.PluginsFile, File.Exists(creationClub) ? creationClub : null, data, throwOnMissingMods: false, fileSystem: fileSystem);
        var loadOrder = global::Mutagen.Bethesda.Plugins.Order.LoadOrder.Import<ISkyrimModGetter>(data, listings.Where(l => l.Enabled), release, fileSystem);
        return new LoadOrderSnapshot(source, resolved, loadOrder, fileSystem);
    }

    /// <summary>A fresh snapshot of the same source. The caller disposes this one.</summary>
    public LoadOrderSnapshot Reopen() => Open(Source);

    /// <summary>Why the load order on disk no longer matches this snapshot, or null when it still does.</summary>
    public string? StaleReason()
    {
        foreach (var (path, stamp) in StampsOf(_stamps.Keys))
        {
            if (_stamps[path] != stamp) return $"{Path.GetFileName(path)} changed since the load order was read";
        }
        return null;
    }

    public void Dispose() => LoadOrder.Dispose();

    private static Dictionary<string, DateTime> Stamps(ResolvedSource resolved, IEnumerable<string> plugins) =>
        StampsOf(resolved.WatchFiles.Concat(plugins.Select(p => resolved.View.TopFiles.GetValueOrDefault(p)?.RealPath).OfType<string>()));

    /// <summary>
    /// Each file's last write time, <see cref="DateTime.MinValue"/> when it is missing. Every query checks these, and a
    /// load order can have thousands of plugins, so each folder is listed once rather than each file asked for in turn.
    /// A listing shows a file's time as of when it was last closed, which is when a mod manager has finished with it.
    /// </summary>
    private static Dictionary<string, DateTime> StampsOf(IEnumerable<string> paths)
    {
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in paths.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var byName = folder.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            foreach (var path in byName.Values) stamps[path] = DateTime.MinValue;
            if (!Directory.Exists(folder.Key)) continue;
            var listing = new System.IO.Enumeration.FileSystemEnumerable<(string Name, DateTime Stamp)>(
                folder.Key, (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.FileName.ToString(), entry.LastWriteTimeUtc.UtcDateTime),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) => !entry.IsDirectory,
            };
            foreach (var (name, stamp) in listing)
            {
                if (byName.TryGetValue(name, out var path)) stamps[path] = stamp;
            }
        }
        return stamps;
    }
}
