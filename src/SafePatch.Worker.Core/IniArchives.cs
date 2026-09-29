using System.IO.Abstractions;
using Mutagen.Bethesda.Archives.DI;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Environments.DI;
using Mutagen.Bethesda.Inis.DI;
using Mutagen.Bethesda.Plugins.Order.DI;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Synthesis.CLI;

namespace SafePatch.Worker.Core;

/// <summary>
/// Synthesis finds the game INI under My Documents, which does not resolve inside the AppContainer, so
/// its asset provider would only see archives named after plugins. The host shares the INI; this gives
/// the program the pipeline's own state with an asset provider that reads the INI's archive lists.
/// </summary>
internal static class IniArchives
{
    public static IPatcherState<ISkyrimMod, ISkyrimModGetter> WithIniArchives(
        IPatcherState<ISkyrimMod, ISkyrimModGetter> state, RunSynthesisMutagenPatcher run, IFileSystem fileSystem, string gameIniPath)
    {
        // Built as Synthesis's PatcherStateFactory builds it, with the INI path injected rather than looked up.
        var dataDirectory = new DataDirectoryInjection(run.DataFolderPath);
        var release = new GameReleaseInjection(run.GameRelease);
        var archiveExtension = new ArchiveExtensionProvider(release);
        var listings = new LoadOrderListingsInjection(state.LoadOrder.ListedOrder.Where(l => l.ModKey != state.PatchMod.ModKey));
        var assets = new GameAssetProvider(
            new DataDirectoryAssetProvider(fileSystem, dataDirectory),
            new ArchiveAssetProvider(
                fileSystem,
                new GetApplicableArchivePaths(
                    fileSystem,
                    new CheckArchiveApplicability(archiveExtension),
                    dataDirectory,
                    archiveExtension,
                    new CachedArchiveListingDetailsProvider(
                        listings,
                        new GetArchiveIniListings(fileSystem, new IniPathInjection(gameIniPath)),
                        new ArchiveNameFromModKeyProvider(release))),
                release));

#pragma warning disable CS0618 // SynthesisState is the state class the pipeline itself creates.
        var copy = new SynthesisState<ISkyrimMod, ISkyrimModGetter>(
            run, state.RawLoadOrder, state.LoadOrder, state.LinkCache, assets, state.PatchMod,
            state.ExtraSettingsDataPath, state.InternalDataPath, state.DefaultSettingsDataPath, state.Cancel,
            formKeyAllocator: null); // Internal to Synthesis; the pipeline commits the original state's allocator.
#pragma warning restore CS0618
        EnsureOnlyTheAssetProviderDiffers(state, copy);
        return copy;
    }

    /// <summary>
    /// Fails if a Synthesis update added state the copy does not carry: every other member the program can
    /// see must be the pipeline's own. The pipeline still writes, and disposes, its original state.
    /// </summary>
    private static void EnsureOnlyTheAssetProviderDiffers(IPatcherState<ISkyrimMod, ISkyrimModGetter> original, IPatcherState<ISkyrimMod, ISkyrimModGetter> copy)
    {
        var properties = typeof(IPatcherState<ISkyrimMod, ISkyrimModGetter>).GetInterfaces()
            .Append(typeof(IPatcherState<ISkyrimMod, ISkyrimModGetter>))
            .SelectMany(i => i.GetProperties())
            .Where(p => p.Name != nameof(IPatcherState.AssetProvider));
        foreach (var property in properties)
        {
            if (!Equals(property.GetValue(original), property.GetValue(copy)))
                throw new InvalidOperationException($"The program's state differs from Synthesis's in {property.DeclaringType?.Name}.{property.Name}.");
        }
    }
}
