using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;

namespace SafePatch.Authoring;

/// <summary>
/// A load order read the way Synthesis reads it: a Data folder and a plugins.txt, plus the game's
/// implicit masters. Plugins are opened read-only as overlays; nothing is ever written to them.
/// </summary>
public sealed class LoadOrderSnapshot : IDisposable
{
    private LoadOrderSnapshot(string dataFolder, string pluginsFile, GameRelease release, ILoadOrder<IModListing<ISkyrimModGetter>> loadOrder)
    {
        DataFolder = dataFolder;
        PluginsFile = pluginsFile;
        Release = release;
        LoadOrder = loadOrder;
        Mods = [.. loadOrder.ListedOrder.Where(l => l.Mod is not null).Select(l => l.Mod!)];
        LinkCache = loadOrder.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
    }

    public string DataFolder { get; }
    public string PluginsFile { get; }
    public GameRelease Release { get; }
    public ILoadOrder<IModListing<ISkyrimModGetter>> LoadOrder { get; }

    /// <summary>The plugins that are present, lowest priority first.</summary>
    public IReadOnlyList<ISkyrimModGetter> Mods { get; }

    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache { get; }

    public static LoadOrderSnapshot Open(string dataFolder, string pluginsFile, GameRelease release = GameRelease.SkyrimSE)
    {
        if (release.ToCategory() != GameCategory.Skyrim) throw new SafePatchException($"SafePatch supports Skyrim releases only, not {release}.");
        if (!Directory.Exists(dataFolder)) throw new SafePatchException($"Data folder {dataFolder} does not exist.");
        if (!File.Exists(pluginsFile)) throw new SafePatchException($"Plugins file {pluginsFile} does not exist.");

        var listings = global::Mutagen.Bethesda.Plugins.Order.LoadOrder.GetLoadOrderListings(
            release, pluginsFile, creationClubFilePath: null, dataFolder, throwOnMissingMods: false);
        var loadOrder = global::Mutagen.Bethesda.Plugins.Order.LoadOrder.Import<ISkyrimModGetter>(dataFolder, listings.Where(l => l.Enabled), release);
        return new LoadOrderSnapshot(Path.GetFullPath(dataFolder), Path.GetFullPath(pluginsFile), release, loadOrder);
    }

    public void Dispose() => LoadOrder.Dispose();
}
