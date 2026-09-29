using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Plugins.Analysis.DI;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using SafePatch.Host;

namespace SafePatch.Mutagen;

/// <summary>
/// Reads the worker's output plugin, and re-reads baseline records, in exactly the same way: as
/// path-based overlays of a plugin keyed like the patch. Mutagen's parse of some fields depends on
/// context (e.g. Skyrim item owners in another plugin read as untyped), so comparing records read
/// differently would report changes that are not there. Both sides use the run's
/// <see cref="PluginFormat"/>: its language and string encoding, and split parts when a plugin needs
/// too many masters. Temporary files are removed on dispose.
/// </summary>
public sealed class ModCodec(GameRelease release, ModKey patchKey, PluginFormat? format = null) : IDisposable
{
    private readonly PluginFormat _format = format ?? PluginFormat.Default;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("SafePatch-");
    private readonly List<IDisposable> _open = [];
    private int _files;

    /// <summary>Parses plugin bytes as the patch mod: the plugin, then its split parts (<c>_2</c>, <c>_3</c>...), if any.</summary>
    public IModGetter Read(IReadOnlyList<byte[]> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("A plugin has at least one part.", nameof(parts));
        if (parts.Count > 1 && !_format.Split)
            throw new PatchRejectedException("The worker split its plugin, but this run does not split plugins.");
        foreach (var part in parts) PluginFraming.Validate(part, release);
        var path = NextPath();
        for (var i = 0; i < parts.Count; i++)
            File.WriteAllBytes(PartPath(path, i), parts[i]);
        return Open(path);
    }

    /// <summary>Parses a single plugin as the patch mod.</summary>
    public IModGetter Read(byte[] plugin) => Read([plugin]);

    /// <summary>
    /// Copies records into a plugin keyed like the patch, writes it and reads it back. Each record is
    /// added through its context, parents first, so nested records land inside their parents exactly
    /// as <c>GetOrAddAsOverride</c> puts them in a patch.
    /// </summary>
    public IModGetter RoundTrip<TMod, TModGetter>(IEnumerable<IModContext<TMod, TModGetter, IMajorRecord, IMajorRecordGetter>> records)
        where TModGetter : class, IModGetter
        where TMod : class, TModGetter, IMod, IMajorRecordContextEnumerable<TMod, TModGetter>
    {
        var mod = (TMod)ModFactory.Activator(patchKey, release);
        foreach (var record in records.OrderBy(Depth))
            RecordDiff.CopyIn(record.GetOrAddAsOverride(mod), record.Record);
        var path = NextPath();
        if (_format.Split) new AutoSplitModWriter(new MultiModFileSplitter()).Write<TMod, TModGetter>(mod, path, _format.WriteParameters);
        else mod.WriteToBinary(path, _format.WriteParameters);
        return Open(path);
    }

    /// <summary>How many records contain this one (0 for a top-level record).</summary>
    public static int Depth(IModContext context)
    {
        var depth = 0;
        for (var parent = context.Parent; parent is not null; parent = parent.Parent)
            if (parent.Record is IMajorRecordGetter) depth++;
        return depth;
    }

    private IModGetter Open(string path)
    {
        var modPath = new ModPath(patchKey, path);
        var parts = _format.Split ? MultiModFileAnalysis.GetSplitModFiles(modPath) : [];
        IModDisposeGetter mod = parts.Count > 0
            ? ModFactory.ImportMultiFileGetter(patchKey, parts.Select(p => new ModPath(patchKey, p.Path)), _format.LoadOrder ?? [], release, _format.ReadParameters)
            : ModFactory.ImportGetter(modPath, release, _format.ReadParameters);
        _open.Add(mod);
        return mod;
    }

    /// <summary>Where Mutagen's split writer puts part <paramref name="index"/> of <paramref name="path"/>.</summary>
    private static string PartPath(string path, int index) => index == 0
        ? path
        : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_{index + 1}{Path.GetExtension(path)}");

    private string NextPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(_directory.FullName, (_files++).ToString())).FullName, patchKey.FileName);

    public void Dispose()
    {
        foreach (var mod in _open) mod.Dispose();
        try { _directory.Delete(recursive: true); } catch (IOException) { }
    }
}
