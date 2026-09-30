using SafePatch.Host;
using SafePatch.Synthesis;

namespace SafePatch.Authoring.Sources;

/// <summary>One folder layered into the Data folder: the game's own, an MO2 mod, or MO2's overwrite.</summary>
public sealed record DataLayer(string Name, string Root);

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
            var real = Path.Combine(Layers[i].Root, relative);
            if (File.Exists(real)) found.Add(new DataFile(relative, real, Layers[i]));
        }
        return found;
    }

    /// <summary>Where a Data-relative file appears to be, to the game and the worker.</summary>
    public string VirtualPath(string dataRelativePath) => Path.Combine(DataFolder, dataRelativePath);

    /// <summary>Whether a path is inside the Data folder or any of its layers.</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        return new[] { DataFolder }.Concat(Layers.Select(l => l.Root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase)
                         || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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
        return files;
    }
}
