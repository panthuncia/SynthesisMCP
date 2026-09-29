using Microsoft.Win32.SafeHandles;

namespace SafePatch.Host;

/// <summary>Reads loose files in the Data folder for the broker. Real: the Data folder on disk (through MO2's VFS when running under MO2).</summary>
public interface IAssetSource
{
    /// <summary>Opens a normalized Data-relative file for reading, or returns null when there is no such file.</summary>
    SafeFileHandle? Open(string dataRelativePath);
}

/// <summary>
/// Serves the worker's requests for loose Data files. Every request is normalized, checked
/// against the manifest's asset patterns and the budgets, and only then opened. A request the
/// patterns do not allow is answered as missing, without touching the disk.
/// </summary>
public sealed class AssetBroker(IAssetSource source, PatchPolicy policy)
{
    private const int MaxReportedDenials = 50;
    private readonly List<string> _denied = [];
    private int _requests;
    private long _bytes;

    /// <summary>Paths the program asked for that the manifest does not allow (the first few), for the report.</summary>
    public IReadOnlyList<string> Denied => _denied;

    /// <summary>An open handle to the file, or null when it is missing or not allowed.</summary>
    /// <exception cref="PatchRejectedException">The path is malformed or a budget is exhausted.</exception>
    public SafeFileHandle? Open(string requestedPath)
    {
        if (++_requests > policy.MaxAssetRequests)
            throw new PatchRejectedException($"The program read more than {policy.MaxAssetRequests} assets.");
        var path = AssetPath.Normalize(requestedPath)
                   ?? throw new PatchRejectedException($"The program asked for an asset with a malformed path: '{Printable(requestedPath)}'.");

        if (!policy.CanRead(path))
        {
            if (_denied.Count < MaxReportedDenials && !_denied.Contains(path)) _denied.Add(path);
            return null;
        }

        var handle = source.Open(path);
        if (handle is null) return null;
        _bytes += RandomAccess.GetLength(handle);
        if (_bytes > policy.MaxAssetBytes)
        {
            handle.Dispose();
            throw new PatchRejectedException($"The program read more than {policy.MaxAssetBytes} bytes of assets.");
        }
        return handle;
    }

    private static string Printable(string text) =>
        new(text.Take(200).Select(c => char.IsControl(c) ? '?' : c).ToArray());
}

/// <summary>Data-relative asset paths, as the host accepts them.</summary>
public static class AssetPath
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A plain relative path of ordinary names separated by <c>\</c>, or null. Rejects rooted
    /// paths, drive and stream separators (<c>:</c>), <c>.</c> and <c>..</c>, device names,
    /// names ending in a dot or space, wildcards and control characters.
    /// </summary>
    public static string? Normalize(string path)
    {
        if (path.Length is 0 or > 260) return null;
        var segments = path.Replace('/', '\\').Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ' || segment[0] == ' ') return null;
            if (segment.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) return null;
            if (DeviceNames.Contains(segment.Split('.')[0])) return null;
        }
        return string.Join('\\', segments);
    }
}

/// <summary>
/// A Data-relative glob, case-insensitive: <c>*</c> and <c>?</c> match within one name, <c>**</c>
/// matches any number of folders, e.g. <c>meshes/**/*.nif</c>.
/// </summary>
public sealed record AssetPattern(string Text)
{
    private string[] Segments { get; } = Text.Split('\\');

    public static AssetPattern Parse(string text)
    {
        var normalized = text.Replace('/', '\\');
        var valid = normalized.Length is > 0 and <= 260
                    && normalized.Split('\\').All(s => s.Length > 0 && s is not "." and not ".." && !s.Any(c => char.IsControl(c) || c is ':' or '"' or '<' or '>' or '|'));
        return valid ? new AssetPattern(normalized) : throw new SafePatchException($"'{text}' is not a Data-relative asset pattern.");
    }

    /// <param name="path">A path from <see cref="AssetPath.Normalize"/>.</param>
    public bool Matches(string path) => Match(Segments, path.Split('\\'));

    private static bool Match(ReadOnlySpan<string> pattern, ReadOnlySpan<string> path)
    {
        if (pattern.IsEmpty) return path.IsEmpty;
        if (pattern[0] == "**")
        {
            for (var skip = 0; skip <= path.Length; skip++)
                if (Match(pattern[1..], path[skip..])) return true;
            return false;
        }
        return !path.IsEmpty && MatchName(pattern[0], path[0]) && Match(pattern[1..], path[1..]);
    }

    private static bool MatchName(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        if (pattern.IsEmpty) return name.IsEmpty;
        if (pattern[0] == '*')
        {
            for (var skip = 0; skip <= name.Length; skip++)
                if (MatchName(pattern[1..], name[skip..])) return true;
            return false;
        }
        return !name.IsEmpty
               && (pattern[0] == '?' || char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(name[0]))
               && MatchName(pattern[1..], name[1..]);
    }

    public override string ToString() => Text;
}

/// <summary>
/// Loose files under a Data folder. Refuses anything that resolves outside it and anything
/// reached through a reparse point below it (junctions and symbolic links).
/// </summary>
public sealed class DataFolderAssetSource(string dataFolder) : IAssetSource
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));

    public SafeFileHandle? Open(string dataRelativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, dataRelativePath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

        for (var current = full; current.Length > _root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = current == full ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists) return null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        }
        if (Directory.Exists(full)) return null;

        try
        {
            return File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
