namespace SafePatch.Worker.Core.Tests;

public sealed class BrokeredFileSystemTests : IDisposable
{
    private readonly string _real = Path.Combine(Path.GetTempPath(), $"SafePatchBrokered-{Guid.NewGuid():N}.bin");
    private readonly FileStream _handle;
    private readonly BrokeredFileSystem _fs = new();
    private const string Virtual = @"C:\Game\Data\Skyrim.esm";

    public BrokeredFileSystemTests()
    {
        File.WriteAllBytes(_real, [1, 2, 3, 4, 5]);
        _handle = new FileStream(_real, FileMode.Open, FileAccess.Read, FileShare.Read);
        _fs.AddBrokered(Virtual, BrokeredFileSystem.FromHandle(_handle.SafeFileHandle.DangerousGetHandle().ToInt64()));
    }

    [Fact]
    public void Brokered_files_exist_and_stream_their_real_contents()
    {
        Assert.True(_fs.File.Exists(Virtual));
        Assert.Contains(Virtual, _fs.Directory.EnumerateFiles(@"C:\Game\Data"));
        Assert.Equal([1, 2, 3, 4, 5], _fs.File.ReadAllBytes(Virtual));

        using var stream = _fs.FileStream.New(Virtual, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(5, stream.Length);
    }

    [Fact]
    public void Concurrent_readers_of_one_handle_keep_their_own_positions()
    {
        using var a = _fs.File.OpenRead(Virtual);
        using var b = _fs.File.OpenRead(Virtual);
        a.ReadExactly(new byte[3]);
        Assert.Equal(1, b.ReadByte());
        Assert.Equal(4, a.ReadByte());
    }

    [Theory]
    [InlineData(FileMode.Open, FileAccess.Write)]
    [InlineData(FileMode.Open, FileAccess.ReadWrite)]
    [InlineData(FileMode.Create, FileAccess.Write)]
    [InlineData(FileMode.Truncate, FileAccess.Write)]
    public void Brokered_files_cannot_be_written(FileMode mode, FileAccess access) =>
        Assert.Throws<UnauthorizedAccessException>(() => _fs.FileStream.New(Virtual, mode, access));

    [Fact]
    public void Ordinary_files_are_in_memory_and_writable()
    {
        const string output = @"C:\Output\Synthesis.esp";
        _fs.Directory.CreateDirectory(@"C:\Output");
        using (var stream = _fs.FileStream.New(output, FileMode.Create, FileAccess.Write)) stream.Write([9, 9]);

        Assert.Equal([9, 9], _fs.File.ReadAllBytes(output));
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        _handle.Dispose();
        File.Delete(_real);
    }
}
