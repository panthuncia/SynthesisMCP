using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Win32.SafeHandles;

namespace SafePatch.Authoring.Sources;

/// <summary>
/// The file system Mutagen reads a <see cref="DataView"/> through: the view's plugins, archives and strings files
/// at their Data folder paths, plus the load order's own files (plugins.txt). Each is an empty placeholder whose
/// contents stream from the real file, opened with read, write and delete sharing: a snapshot that holds plugins
/// open never stops a mod manager from deleting a mod or renaming a plugin away. Everything is read-only.
/// </summary>
internal sealed class DataViewFileSystem : MockFileSystem
{
    private readonly Dictionary<string, string> _real = new(StringComparer.OrdinalIgnoreCase);

    public DataViewFileSystem(DataView view, IEnumerable<string> extraFiles) : base(new MockFileSystemOptions { CreateDefaultTempDir = false })
    {
        File = new ViewFile(this);
        FileStream = new ViewFileStreamFactory(this, base.FileStream);
        FileInfo = new ViewFileInfoFactory(this, base.FileInfo);

        AddDirectory(view.DataFolder);
        foreach (var file in view.TopFiles.Values) Add(view.VirtualPath(file.RelativePath), file.RealPath);
        foreach (var file in extraFiles) Add(file, file);
    }

    public override IFile File { get; }
    public override IFileStreamFactory FileStream { get; }
    public override IFileInfoFactory FileInfo { get; }

    private void Add(string path, string real)
    {
        var full = Path.GetFullPath(path);
        AddFile(full, new MockFileData([]));
        _real[full] = real;
    }

    private FileSystemStream? TryOpen(string path, FileMode mode, FileAccess access)
    {
        var full = Path.GetFullPath(path);
        if (!_real.TryGetValue(full, out var real)) return null;
        if (mode != FileMode.Open || access != FileAccess.Read) throw new UnauthorizedAccessException($"{path} is read-only.");
        return new ViewStream(new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), full);
    }

    private long? RealLength(string path) =>
        _real.TryGetValue(Path.GetFullPath(path), out var real) ? new FileInfo(real).Length : null;

    private sealed class ViewStream(Stream inner, string path) : FileSystemStream(inner, path, isAsync: false);

    private sealed class ViewFile(DataViewFileSystem fs) : MockFile(fs)
    {
        public override FileSystemStream OpenRead(string path) => fs.TryOpen(path, FileMode.Open, FileAccess.Read) ?? base.OpenRead(path);

        public override FileSystemStream Open(string path, FileMode mode) =>
            fs.TryOpen(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) ?? base.Open(path, mode);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access) => fs.TryOpen(path, mode, access) ?? base.Open(path, mode, access);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            fs.TryOpen(path, mode, access) ?? base.Open(path, mode, access, share);

        public override FileSystemStream Open(string path, FileStreamOptions options) => fs.TryOpen(path, options.Mode, options.Access) ?? base.Open(path, options);

        public override byte[] ReadAllBytes(string path)
        {
            using var stream = fs.TryOpen(path, FileMode.Open, FileAccess.Read);
            if (stream is null) return base.ReadAllBytes(path);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public override string[] ReadAllLines(string path) => ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

        public override string ReadAllText(string path)
        {
            using var stream = fs.TryOpen(path, FileMode.Open, FileAccess.Read);
            if (stream is null) return base.ReadAllText(path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    private sealed class ViewFileInfo(DataViewFileSystem fs, string path) : MockFileInfo(fs, path)
    {
        public override long Length => fs.RealLength(FullName) ?? base.Length;
    }

    private sealed class ViewFileInfoFactory(DataViewFileSystem fs, IFileInfoFactory inner) : IFileInfoFactory
    {
        public IFileSystem FileSystem => fs;
        public IFileInfo New(string fileName) => new ViewFileInfo(fs, fileName);
        public IFileInfo? Wrap(FileInfo? fileInfo) => inner.Wrap(fileInfo);
    }

    private sealed class ViewFileStreamFactory(DataViewFileSystem fs, IFileStreamFactory inner) : IFileStreamFactory
    {
        public IFileSystem FileSystem => fs;

        public FileSystemStream New(string path, FileMode mode) =>
            fs.TryOpen(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) ?? inner.New(path, mode);

        public FileSystemStream New(string path, FileMode mode, FileAccess access) => fs.TryOpen(path, mode, access) ?? inner.New(path, mode, access);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) =>
            fs.TryOpen(path, mode, access) ?? inner.New(path, mode, access, share);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) =>
            fs.TryOpen(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) =>
            fs.TryOpen(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize, useAsync);

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
            fs.TryOpen(path, mode, access) ?? inner.New(path, mode, access, share, bufferSize, options);

        public FileSystemStream New(string path, FileStreamOptions options) => fs.TryOpen(path, options.Mode, options.Access) ?? inner.New(path, options);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => inner.New(handle, access);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => inner.New(handle, access, bufferSize);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => inner.New(handle, access, bufferSize, isAsync);
        public FileSystemStream Wrap(FileStream fileStream) => inner.Wrap(fileStream);
    }
}
