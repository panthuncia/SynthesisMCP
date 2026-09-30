using Mutagen.Bethesda;
using SafePatch.Host;
using SafePatch.Synthesis;

namespace SafePatch.Authoring.Sources;

/// <summary>Where a load order comes from: a plain Data folder, or a Mod Organizer 2 profile.</summary>
public abstract record LoadOrderSource
{
    /// <summary>Reads the source's configuration. Only ever reads.</summary>
    public abstract ResolvedSource Resolve();
}

/// <summary>A load order's files, resolved from its source.</summary>
/// <param name="PluginsFile">The plugins.txt listing the enabled plugins.</param>
/// <param name="GameIni">The game INI listing archives, when the source chooses one; null means Synthesis's lookup.</param>
/// <param name="WatchFiles">Configuration files whose changes make a snapshot stale.</param>
/// <param name="ProtectedFolders">Folders nothing may be written into: the Data folder's layers, the mod manager's instance.</param>
/// <param name="Warnings">Anything odd found while resolving, e.g. an enabled mod whose folder is missing.</param>
public sealed record ResolvedSource(
    string Description, GameRelease Release, DataView View, string PluginsFile, string? GameIni,
    IReadOnlyList<string> WatchFiles, IReadOnlyList<string> ProtectedFolders, IReadOnlyList<string> Warnings);

/// <summary>A game's Data folder and a plugins.txt, as Synthesis reads them without a mod manager.</summary>
public sealed record DataFolderSource(string DataFolder, string PluginsFile, GameRelease Release = GameRelease.SkyrimSE) : LoadOrderSource
{
    public override ResolvedSource Resolve()
    {
        if (!Directory.Exists(DataFolder)) throw new SafePatchException($"Data folder {DataFolder} does not exist.");
        if (!File.Exists(PluginsFile)) throw new SafePatchException($"Plugins file {PluginsFile} does not exist.");
        var data = Path.GetFullPath(DataFolder);
        var plugins = Path.GetFullPath(PluginsFile);
        return new ResolvedSource($"Data folder {data}", Release, new DataView(data, [new DataLayer("Data", data)]), plugins, GameIni: null,
            [plugins], [data], []);
    }
}
