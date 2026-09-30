using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using SafePatch.Authoring.Output;
using SafePatch.Host;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

public enum LinkDirection
{
    Both,
    /// <summary>Records this one links to.</summary>
    Out,
    /// <summary>Records linking to this one.</summary>
    In,
}

public sealed partial class QueryService
{
    /// <summary>
    /// The records a record links to (its winning version's links) and the records whose winning versions link to it:
    /// found by a raw search of the plugins that master it, then confirmed with Mutagen; <paramref name="types"/>
    /// narrows them.
    /// </summary>
    public ResultSet References(string id, LinkDirection direction = LinkDirection.Both, IReadOnlyList<string>? types = null) => Timed(() =>
    {
        var chain = Resolve(id);
        var rows = new List<IReadOnlyList<string?>>();

        if (direction != LinkDirection.In && Winner(chain) is { } record)
        {
            foreach (var link in record.EnumerateFormLinks().Where(l => !l.IsNull).DistinctBy(l => l.FormKey))
            {
                rows.Add(Index.Find(link.FormKey) is { } target
                    ? ["out", link.FormKey.ToString(), TypeOf(target), target.EditorId, Index.Plugins[target.Winner].FileName]
                    : ["out", link.FormKey.ToString(), TypeName(link.Type), null, "(not in the load order)"]);
            }
        }
        if (direction != LinkDirection.Out)
        {
            foreach (var candidate in Index.WinnersMentioning(chain.FormKey, Narrowed(Signatures(types), LinkingTo(chain.Signature))))
            {
                if (Winner(candidate) is { } linking && linking.EnumerateFormLinks().Any(l => l.FormKey == chain.FormKey))
                    rows.Add(["in", candidate.FormKey.ToString(), TypeOf(candidate), candidate.EditorId, Index.Plugins[candidate.Winner].FileName]);
            }
        }

        return new ResultSet($"Links of {chain.FormKey} {chain.EditorId}", ["Direction", "FormKey", "Type", "EditorID", "Winner"], rows,
            [ResultSet.GroupBy("direction", rows.Select(r => r[0])), ResultSet.GroupBy("type", rows.Select(r => r[2]))], Hint: "add types or a direction");
    });

    /// <summary>
    /// Where a Data file comes from: every loose copy (with the mod it is in) and every archive holding it, the winner
    /// first; and the winning records that use it (found by a raw search for its file name, confirmed with Mutagen),
    /// narrowed by <paramref name="recordTypes"/>.
    /// </summary>
    public ResultSet FindAsset(string path, IReadOnlyList<string>? recordTypes = null) => Timed(() =>
    {
        var relative = AssetPath.Normalize(path.Trim()) ?? throw new SafePatchException($"{path} is not a Data-relative path, e.g. meshes\\armor\\iron\\cuirass.nif.");
        var rows = new List<IReadOnlyList<string?>>();

        // Loose files beat every archive; among archives, the later loaded wins.
        foreach (var file in snapshot.View.Providers(relative))
            rows.Add(["provides", "loose", file.Layer.Name, file.RealPath]);
        foreach (var archive in Archives().Reverse())
        {
            if (Contains(archive.RealPath, relative)) rows.Add(["provides", "archive", archive.Layer.Name, archive.RealPath]);
        }
        if (rows.Count > 0) rows[0] = ["wins", .. rows[0].Skip(1)];

        var notes = new List<string>();
        if (rows.Count == 0) notes.Add("No loose file or loaded archive has it.");
        foreach (var candidate in Index.WinnersContainingText(Path.GetFileName(relative), Narrowed(Signatures(recordTypes), AssetHolders.Value)))
        {
            if (Winner(candidate) is { } user && AssetPaths(user).Contains(relative, StringComparer.OrdinalIgnoreCase))
                rows.Add(["used by", TypeOf(candidate), candidate.EditorId, candidate.FormKey.ToString()]);
        }
        return new ResultSet($"Asset {relative}", ["Role", "Kind", "From", "Path"], rows, Notes: notes);
    });

    /// <summary>
    /// The signatures of the record types that can link to a record with <paramref name="target"/>'s, from the links
    /// each type declares (<see cref="LinkTypes"/>), or null for all. Links are confirmed with Mutagen, so a type
    /// without a link its target could fill can never be found: most of a load order's compressed data (landscape and
    /// navmeshes) is only read when the target is something they link to.
    /// </summary>
    private static IReadOnlySet<uint>? LinkingTo(uint target) =>
        RecordTypes.ClassOf(target) is { } targetClass
            ? LinkingSignatures.GetOrAdd(targetClass, t => RecordTypes.All.Where(name => LinkTypes.MayLinkTo(RecordTypes.Class(name), t)).SelectMany(RecordTypes.Signatures).ToHashSet())
            : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlySet<uint>> LinkingSignatures = new();

    /// <summary>
    /// The signatures of the record types with asset links (<see cref="LinkTypes.MayHoldAssets"/>): Mutagen confirms a
    /// use only in those, and landscape and navmeshes, most of a load order's compressed data, have none.
    /// </summary>
    private static readonly Lazy<IReadOnlySet<uint>> AssetHolders = new(() =>
        RecordTypes.All.Where(name => LinkTypes.MayHoldAssets(RecordTypes.Class(name))).SelectMany(RecordTypes.Signatures).ToHashSet());

    /// <summary>The signatures both allow, where null allows all.</summary>
    private static IReadOnlySet<uint>? Narrowed(IReadOnlySet<uint>? asked, IReadOnlySet<uint>? possible) =>
        asked is null ? possible : possible is null ? asked : asked.Where(possible.Contains).ToHashSet();

    /// <summary>The Data-relative asset paths a record lists (models, textures, sounds, scripts...).</summary>
    internal static IEnumerable<string> AssetPaths(IMajorRecordGetter record) =>
        record is IAssetLinkContainerGetter container
            ? container.EnumerateAssetLinks(AssetLinkQuery.Listed).Where(l => !l.IsNull).Select(l => l.DataRelativePath.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>The archives the game loads, lowest priority first: those the INI lists, then each plugin's, in load order.</summary>
    private IEnumerable<Sources.DataFile> Archives()
    {
        var view = snapshot.View;
        var archives = view.TopFiles.Values.Where(f => f.RelativePath.EndsWith(Archive.GetExtension(snapshot.Release), StringComparison.OrdinalIgnoreCase)).ToList();
        var ini = snapshot.GameIni is { Length: > 0 } explicitIni ? explicitIni : snapshot.GameIni is null ? Synthesis.SynthesisInputs.DefaultGameIni(snapshot.Release, view.DataFolder) : null;
        var listed = ini is not null && File.Exists(ini) ? Archive.GetIniListings(snapshot.Release, ini, null).Select(f => f.String).ToList() : [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in listed)
        {
            if (archives.FirstOrDefault(a => a.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } archive && seen.Add(archive.RelativePath)) yield return archive;
        }
        foreach (var mod in snapshot.Mods)
        {
            foreach (var archive in archives.Where(a => Archive.IsApplicable(snapshot.Release, mod.ModKey, a.RelativePath)).OrderBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(archive.RelativePath)) yield return archive;
            }
        }
    }

    private bool Contains(string archivePath, string relative)
    {
        try
        {
            var reader = Archive.CreateReader(snapshot.Release, archivePath);
            var folder = Path.GetDirectoryName(relative) ?? "";
            return reader.TryGetFolder(folder, out var contents)
                   && contents.Files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)
                                              || Path.GetFileName(f.Path).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotImplementedException)
        {
            return false;
        }
    }
}
