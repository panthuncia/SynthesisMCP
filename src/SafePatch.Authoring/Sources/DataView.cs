using SafePatch.Host;
using SafePatch.Sandbox.Windows;
using SafePatch.Synthesis;

namespace SafePatch.Authoring.Sources;

/// <summary>One folder layered into the Data folder: the game's own, an MO2 mod, or MO2's overwrite.</summary>
/// <param name="Virtual">The folder is seen through MO2's virtual file system, which shows every layer's files in it
/// (the game's Data folder, from a process MO2 started). Only the files that really live there are this layer's.</param>
public sealed record DataLayer(string Name, string Root, bool Virtual = false)
{
    /// <summary>The game's Data folder, virtual when this process runs inside MO2's virtual file system.</summary>
    public static DataLayer GameData(string root) => new("Data", root, Virtual: UnderMo2);

    /// <summary>Whether this process runs inside MO2's virtual file system.</summary>
    public static bool UnderMo2 => OperatingSystem.IsWindows() && Mo2Vfs.Active;
}

/// <summary>A file in the Data folder, and where it really lives.</summary>
public sealed record DataFile(string RelativePath, string RealPath, DataLayer Layer);

/// <summary>
/// The Data folder as the game sees it: folders layered lowest priority first, the highest layer that has a
/// file providing it (as MO2's virtual file system does). Only ever read. Plugins, archives and strings files
/// are listed up front; loose files are looked up one at a time.
/// </summary>
public sealed class DataView
{
    private readonly Lazy<IReadOnlyDictionary<string, DataFile>> _topFiles;

    /// <param name="dataFolder">The Data folder's path as the game and Synthesis name it.</param>
    public DataView(string dataFolder, IReadOnlyList<DataLayer> lowestFirst)
    {
        DataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        Layers = lowestFirst;
        _topFiles = new(ListTopFiles);
    }

    public string DataFolder { get; }

    /// <summary>Lowest priority first.</summary>
    public IReadOnlyList<DataLayer> Layers { get; }

    /// <summary>Plugins and archives in the Data folder's root, and the files in <c>Strings\</c>, by relative path: the winning copy of each.</summary>
    public IReadOnlyDictionary<string, DataFile> TopFiles => _topFiles.Value;

    /// <summary>The copy of a Data-relative file that wins, or null.</summary>
    public DataFile? Find(string dataRelativePath) => Providers(dataRelativePath).FirstOrDefault();

    /// <summary>Every layer's copy of a Data-relative file, the winner first.</summary>
    public IReadOnlyList<DataFile> Providers(string dataRelativePath)
    {
        if (AssetPath.Normalize(dataRelativePath) is not { } relative) return [];
        var found = new List<DataFile>();
        for (var i = Layers.Count - 1; i >= 0; i--)
        {
            var path = Path.Combine(Layers[i].Root, relative);
            if (!File.Exists(path)) continue;
            if (Layers[i].Virtual && OperatingSystem.IsWindows())
            {
                // MO2 shows the winning copy here; if another layer holds it, that layer lists it.
                if (Mo2Vfs.RealPath(path) is not { } real || Layers.Any(l => !l.Virtual && Inside(real, l.Root))) continue;
                path = real;
            }
            found.Add(new DataFile(relative, path, Layers[i]));
        }
        return found;
    }

    /// <summary>Where a Data-relative file appears to be, to the game and the worker.</summary>
    public string VirtualPath(string dataRelativePath) => Path.Combine(DataFolder, dataRelativePath);

    /// <summary>Whether a path is inside the Data folder or any of its layers.</summary>
    public bool Contains(string path) => new[] { DataFolder }.Concat(Layers.Select(l => l.Root)).Any(root => Inside(path, root));

    private static bool Inside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The files a test run shares with the worker, each at its path under the Data folder.</summary>
    public IReadOnlyList<InputFile> InputFiles() =>
        [.. TopFiles.Values.Select(f => new InputFile(f.RealPath, SharedAs: VirtualPath(f.RelativePath)))];

    private IReadOnlyDictionary<string, DataFile> ListTopFiles()
    {
        var files = new Dictionary<string, DataFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers)
        {
            if (!Directory.Exists(layer.Root)) continue;
            foreach (var file in Directory.EnumerateFiles(layer.Root).Where(f => SynthesisInputs.DataExtensions.Contains(Path.GetExtension(f))))
                files[Path.GetFileName(file)] = new DataFile(Path.GetFileName(file), file, layer);
            var strings = Path.Combine(layer.Root, "Strings");
            if (!Directory.Exists(strings)) continue;
            foreach (var file in Directory.EnumerateFiles(strings))
            {
                var relative = Path.Combine("Strings", Path.GetFileName(file));
                files[relative] = new DataFile(relative, file, layer);
            }
        }
        // Files a virtual layer still provides may live in a mod folder no other layer names: say where.
        if (OperatingSystem.IsWindows())
        {
            foreach (var (relative, file) in files.Where(f => f.Value.Layer.Virtual).ToList())
                if (Mo2Vfs.RealPath(file.RealPath) is { } real) files[relative] = file with { RealPath = real };
        }
        return files;
    }
}
