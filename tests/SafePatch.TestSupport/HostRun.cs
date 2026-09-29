using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.Synthesis;
using SafePatch.Mutagen;

namespace SafePatch.TestSupport;

/// <summary>
/// What SafePatchHost does inside a real Synthesis run: plugin files on disk, the host's patch mod
/// and link cache, and a session running the real worker pipeline (in-process unless a launcher is given).
/// </summary>
public sealed class HostRun : IDisposable
{
    /// <param name="persistenceFolder">Synthesis's shared FormKey allocation folder, to run with persistence (created if missing).</param>
    /// <param name="extraArguments">More Synthesis arguments, e.g. <c>--Localize</c>.</param>
    /// <param name="fillerPlugins">How many more plugins to load after the fixture's, each defining one weapon.</param>
    /// <param name="extraPlugins">More plugins to write and load after the fixture's (and the fillers), in order.</param>
    public HostRun(
        string? previousPatchSource = null, string? persistenceFolder = null, IReadOnlyList<string>? extraArguments = null, int fillerPlugins = 0,
        IReadOnlyList<SkyrimMod>? extraPlugins = null)
    {
        PersistenceFolder = persistenceFolder;
        _extraArguments = extraArguments ?? [];
        if (persistenceFolder is not null)
        {
            Directory.CreateDirectory(persistenceFolder);
            File.WriteAllText(Path.Combine(persistenceFolder, FormKeyPersistence.MarkerFileName), "");
        }
        Root = Directory.CreateTempSubdirectory("SafePatchHostRun-").FullName;
        Plugins = PluginFixture.Write(DataFolder);
        LoadOrderFile = PluginFixture.WriteLoadOrder(Path.Combine(Root, "plugins.txt"));
        var extra = Enumerable.Range(0, fillerPlugins).Select(i =>
        {
            var mod = new SkyrimMod(ModKey.FromFileName($"Filler{i:D4}.esp"), SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew($"WeaponFiller{i:D4}");
            return mod;
        }).Concat(extraPlugins ?? []).ToList();
        foreach (var mod in extra) mod.WriteToBinary(Path.Combine(DataFolder, mod.ModKey.FileName));
        File.AppendAllLines(LoadOrderFile, extra.Select(m => $"*{m.ModKey.FileName}"));
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);

        // Imported the way Synthesis builds state.LoadOrder, so record-type info is available for parsing.
        LoadOrder = global::Mutagen.Bethesda.Plugins.Order.LoadOrder.Import<ISkyrimModGetter>(
            DataFolder,
            new[] { PluginFixture.BaseMaster, PluginFixture.Caco, PluginFixture.Other }.Select(m => ModKey.FromFileName(m)).Concat(extra.Select(m => m.ModKey))
                .Select(m => new LoadOrderListing(m, enabled: true)),
            GameRelease.SkyrimSE);
        Mods = [.. LoadOrder.ListedOrder.Select(l => l.Mod!)];

        // Synthesis seeds the patch mod with the previous patcher's output, when there is one.
        SourcePath = previousPatchSource;
        PatchMod = SourcePath is null
            ? new SkyrimMod(ModKey.FromFileName(Path.GetFileName(OutputPath)), SkyrimRelease.SkyrimSE)
            : SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromFileName(Path.GetFileName(OutputPath)), SourcePath), SkyrimRelease.SkyrimSE);
        LinkCache = Mods.ToMutableLinkCache<ISkyrimMod, ISkyrimModGetter>(PatchMod);
    }

    private readonly IReadOnlyList<string> _extraArguments;

    public string Root { get; }
    public string DataFolder => Path.Combine(Root, "Data");
    public string OutputPath => Path.Combine(Root, "Output", "Synthesis.esp");
    /// <summary>The patcher's extra data folder, where Synthesis keeps its settings.</summary>
    public string ExtraDataFolder => Path.Combine(Root, "ExtraData");
    public string LoadOrderFile { get; }
    /// <summary>The game INI the host shares, when a test writes one; the user's own is never read.</summary>
    public string GameIni => Path.Combine(Root, "Skyrim.ini");
    public string? SourcePath { get; }
    public string? PersistenceFolder { get; }
    public PluginFixture Plugins { get; }
    public ILoadOrder<IModListing<ISkyrimModGetter>> LoadOrder { get; }
    public IReadOnlyList<ISkyrimModGetter> Mods { get; }
    public SkyrimMod PatchMod { get; }
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache { get; }

    /// <summary>The arguments Synthesis would pass this patcher.</summary>
    public IReadOnlyList<string> Arguments =>
        PluginFixture.RunPatcherArguments(DataFolder, LoadOrderFile, OutputPath, SourcePath, ExtraDataFolder, PersistenceFolder, extra: _extraArguments);

    public MutagenPatchCommitter<ISkyrimMod, ISkyrimModGetter> Committer() =>
        new(PatchMod, LinkCache, GameRelease.SkyrimSE, FormKeyPersistence.FromArguments(PersistenceFolder, "SafePatchTest"),
            SynthesisInputs.Format(SynthesisInputs.Parse(Arguments), [.. LoadOrder.ListedOrder.Select(l => l.ModKey), PatchMod.ModKey]));

    public SessionReport Run(
        string programSource, IReadOnlyList<string>? writable = null, IReadOnlyList<string>? creatable = null,
        IWorkerLauncher? launcher = null, IReadOnlyList<string>? assets = null, IReadOnlyList<string>? removable = null) =>
        new PatchSession(launcher ?? InProcessWorkerLauncher.Real(), Committer()).Run(
            TestPrograms.Package(TestPrograms.Compile(programSource), writable, creatable, assets: assets, removable: removable),
            Inputs(),
            CancellationToken.None);

    /// <summary>Runs a program with autogenerated settings, read from <c>settings.json</c> in <see cref="ExtraDataFolder"/>.</summary>
    public SessionReport RunWithSettings(string programSource, string settingsSource, string settingsType, IReadOnlyList<string>? creatable = null)
    {
        var settings = new ManifestSettings(settingsType, "settings.json");
        return new PatchSession(InProcessWorkerLauncher.Real(), Committer()).Run(
            TestPrograms.Package(TestPrograms.Compile(programSource, settingsSource: settingsSource), creatable: creatable, settings: settings),
            Inputs(settings.Path),
            CancellationToken.None);
    }

    public RunInputs Inputs(string? settingsPath = null) =>
        SynthesisInputs.Plan(Arguments, settingsPath, GameIni);

    public void Dispose()
    {
        LoadOrder.Dispose();
        TempFolder.Delete(Root);
    }
}
