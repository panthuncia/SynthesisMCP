using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Win32.SafeHandles;

namespace SafePatch.Worker.Core;

/// <summary>
/// The only file system the Synthesis pipeline sees inside the worker. It is an in-memory
/// <see cref="MockFileSystem"/>: small inputs are copied in, writes (including the output plugin)
/// stay in memory. Large read-only inputs are "brokered": their entries are placeholders whose
/// contents stream from a handle the host shared, so plugins are read lazily rather than copied.
/// Loose assets are brokered on demand: the first read of an unknown path under the Data folder
/// asks the host (see <see cref="SetAssetResolver"/>).
/// </summary>
public sealed class BrokeredFileSystem : MockFileSystem
{
    private readonly Dictionary<string, Func<Stream>> _brokered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _resolving = new();
    private string? _dataFolder;
    private Func<string, Func<Stream>?>? _resolveAsset;

    public BrokeredFileSystem() : base(new MockFileSystemOptions { CreateDefaultTempDir = false })
    {
        File = new BrokeredFile(this);
        FileStream = new BrokeredFileStreamFactory(this, base.FileStream);
        FileInfo = new BrokeredFileInfoFactory(this, base.FileInfo);
    }

    public override IFile File { get; }
    public override IFileStreamFactory FileStream { get; }
    public override IFileInfoFactory FileInfo { get; }

    /// <summary>
    /// Serves reads of files under <paramref name="dataFolder"/> that were not shared up front:
    /// <paramref name="resolve"/> gets the Data-relative path and returns an opener, or null when
    /// the file is missing (or not allowed). Each path is asked for at most once.
    /// </summary>
    public void SetAssetResolver(string dataFolder, Func<string, Func<Stream>?> resolve)
    {
        _dataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        _resolveAsset = resolve;
    }

    /// <summary>Asks for an unknown file under the Data folder, once, before it is read.</summary>
    internal void EnsureResolved(string path)
    {
        if (_resolveAsset is null || _dataFolder is null) return;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_dataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

        lock (_resolving)
        {
            if (FileExists(full) || Directory.Exists(full) || _missing.Contains(full)) return;
            var open = _resolveAsset(Path.GetRelativePath(_dataFolder, full));
            if (open is null) _missing.Add(full);
            else AddBrokered(full, open);
        }
    }

    /// <summary>The real length of a brokered file (its placeholder is empty); null for other files.</summary>
    internal long? BrokeredLength(string path)
    {
        if (!_brokered.TryGetValue(Path.GetFullPath(path), out var open)) return null;
        using var stream = open();
        return stream.Length;
    }

    /// <summary>Adds a read-only file whose contents come from <paramref name="open"/>.</summary>
    public void AddBrokered(string path, Func<Stream> open)
    {
        var full = Path.GetFullPath(path);
        AddFile(full, new MockFileData([]) { AllowedFileShare = FileShare.Read });
        _brokered[full] = open;
    }

    /// <summary>Opens a brokered file for reading; returns null for ordinary in-memory files.</summary>
    internal FileSystemStream? TryOpenBrokered(string path, FileMode mode, FileAccess access)
    {
        if (mode == FileMode.Open && access == FileAccess.Read) EnsureResolved(path);
        var full = Path.GetFullPath(path);
        if (!_brokered.TryGetValue(full, out var open)) return null;
        if (mode != FileMode.Open || access != FileAccess.Read)
            throw new UnauthorizedAccessException($"{path} is read-only in the SafePatch sandbox.");
        return new BrokeredStream(open(), full);
    }

    /// <summary>
    /// Wraps a read-only handle the host duplicated into this process. Every stream over the handle
    /// starts at 0 and keeps its own position (FileStream reads positionally), so concurrent readers
    /// of the same file do not disturb each other.
    /// </summary>
    public static Func<Stream> FromHandle(long handle) => () =>
    {
        var stream = new FileStream(new SafeFileHandle((IntPtr)handle, ownsHandle: false), FileAccess.Read, bufferSize: 0);
        stream.Position = 0;
        return stream;
    };

    private sealed class BrokeredStream(Stream inner, string path) : FileSystemStream(inner, path, isAsync: false);

    private sealed class BrokeredFile(BrokeredFileSystem fs) : MockFile(fs)
    {
        public override bool Exists(string? path)
        {
            if (!string.IsNullOrEmpty(path)) fs.EnsureResolved(path);
            return base.Exists(path);
        }

        public override FileSystemStream OpenRead(string path) =>
            fs.TryOpenBrokered(path, FileMode.Open, FileAccess.Read) ?? base.OpenRead(path);

        public override FileSystemStream Open(string path, FileMode mode) =>
            fs.TryOpenBrokered(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) ?? base.Open(path, mode);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access) =>
            fs.TryOpenBrokered(path, mode, access) ?? base.Open(path, mode, access);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            fs.TryOpenBrokered(path, mode, access) ?? base.Open(path, mode, access, share);

        public override FileSystemStream Open(string path, FileStreamOptions options) =>
            fs.TryOpenBrokered(path, options.Mode, options.Access) ?? base.Open(path, options);

        public override byte[] ReadAllBytes(string path)
        {
            using var stream = fs.TryOpenBrokered(path, FileMode.Open, FileAccess.Read);
            if (stream is null) return base.ReadAllBytes(path);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private sealed class BrokeredFileInfo(BrokeredFileSystem fs, string path) : MockFileInfo(fs, path)
    {
        public override long Length => fs.BrokeredLength(FullName) ?? base.Length;
    }

    private sealed class BrokeredFileInfoFactory(BrokeredFileSystem fs, IFileInfoFactory inner) : IFileInfoFactory
    {
        public IFileSystem FileSystem => fs;

        public IFileInfo New(string fileName)
        {
            fs.EnsureResolved(fileName);
            return new BrokeredFileInfo(fs, fileName);
        }

        public IFileInfo? Wrap(FileInfo? fileInfo) => inner.Wrap(fileInfo);
    }

    private sealed class BrokeredFileStreamFactory(BrokeredFileSystem fs, IFileStreamFactory inner) : IFileStreamFactory
    {
        public IFileSystem FileSystem => fs;

        public FileSystemStream New(string path, FileMode mode) =>
            fs.TryOpenBrokered(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) ?? inner.New(path, mode);

        public FileSystemStream New(string path, FileMode mode, FileAccess access) =>
            fs.TryOpenBrokered(path, mode, access) ?? inner.New(path, mode, access);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) =>
            fs.TryOpenBrokered(path, mode, access) ?? inner.New(path, mode, access, share);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) =>
            fs.TryOpenBrokered(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) =>
            fs.TryOpenBrokered(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize, useAsync);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
            fs.TryOpenBrokered(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize, options);

        public FileSystemStream New(string path, FileStreamOptions options) =>
            fs.TryOpenBrokered(path, options.Mode, options.Access) ?? inner.New(path, options);

        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => inner.New(handle, access);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => inner.New(handle, access, bufferSize);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => inner.New(handle, access, bufferSize, isAsync);
        public FileSystemStream Wrap(FileStream fileStream) => inner.Wrap(fileStream);
    }
}
