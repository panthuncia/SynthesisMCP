using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using SafePatch.Protocol;
using SafePatch.TestSupport;
using SafePatch.Worker.Core;

namespace SafePatch.Host.Tests;

/// <summary>The host serves loose Data files one request at a time, within the manifest's patterns and budgets.</summary>
public sealed class AssetBrokerTests : IDisposable
{
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("SafePatchAssets-");

    private sealed class FakeSource(params string[] files) : IAssetSource
    {
        public List<string> Opened { get; } = [];

        public SafeFileHandle? Open(string dataRelativePath)
        {
            Opened.Add(dataRelativePath);
            if (!files.Contains(dataRelativePath, StringComparer.OrdinalIgnoreCase)) return null;
            var temp = Path.GetTempFileName();
            File.WriteAllBytes(temp, [1, 2, 3]);
            return File.OpenHandle(temp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.DeleteOnClose);
        }
    }

    private static PatchPolicy Policy(params string[] assets) =>
        PatchPolicy.PublisherDefault with { Assets = [.. assets.Select(AssetPattern.Parse)] };

    [Theory]
    [InlineData("meshes/**/*.nif", @"meshes\armor\iron\cuirass.nif", true)]
    [InlineData("meshes/**/*.nif", @"meshes\cuirass.nif", true)]
    [InlineData("meshes/**/*.nif", @"MESHES\Armor\Cuirass.NIF", true)]
    [InlineData("meshes/**/*.nif", @"textures\armor\cuirass.dds", false)]
    [InlineData("scripts/*.pex", @"scripts\sub\x.pex", false)]
    [InlineData("scripts/?.pex", @"scripts\a.pex", true)]
    [InlineData("**", @"anything\at\all.txt", true)]
    public void Patterns_match_data_relative_paths(string pattern, string path, bool expected) =>
        Assert.Equal(expected, AssetPattern.Parse(pattern).Matches(AssetPath.Normalize(path)!));

    [Theory]
    [InlineData(@"..\Skyrim.ini")]
    [InlineData(@"meshes\..\..\x.nif")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\x.nif")]
    [InlineData(@"\meshes\x.nif")]
    [InlineData(@"meshes\x.nif:stream")]
    [InlineData(@"meshes\CON.nif")]
    [InlineData(@"meshes\x.nif.")]
    [InlineData(@"meshes\x.nif ")]
    [InlineData(@"meshes\*.nif")]
    [InlineData(@"meshes\\x.nif")]
    [InlineData("")]
    public void Malformed_paths_fail_the_run_without_touching_the_source(string path)
    {
        var source = new FakeSource();
        var broker = new AssetBroker(source, Policy("**"));

        Assert.Throws<PatchRejectedException>(() => broker.Open(path));
        Assert.Empty(source.Opened);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData("meshes//x.nif")]
    public void Malformed_patterns_are_rejected(string pattern) =>
        Assert.Throws<SafePatchException>(() => AssetPattern.Parse(pattern));

    [Fact]
    public void A_path_outside_the_patterns_reads_as_missing_without_touching_the_source()
    {
        var source = new FakeSource(@"textures\x.dds");
        var broker = new AssetBroker(source, Policy("meshes/**"));

        Assert.Null(broker.Open(@"textures\x.dds"));
        Assert.Empty(source.Opened);
        Assert.Equal([@"textures\x.dds"], broker.Denied);
    }

    [Fact]
    public void Budgets_fail_the_run()
    {
        var source = new FakeSource(@"meshes\a.nif", @"meshes\b.nif");
        var byCount = new AssetBroker(source, Policy("**") with { MaxAssetRequests = 1 });
        using (byCount.Open(@"meshes\a.nif")) { }
        Assert.Throws<PatchRejectedException>(() => byCount.Open(@"meshes\b.nif"));

        var bySize = new AssetBroker(source, Policy("**") with { MaxAssetBytes = 4 });
        using (bySize.Open(@"meshes\a.nif")) { }
        Assert.Throws<PatchRejectedException>(() => bySize.Open(@"meshes\b.nif"));
    }

    [Fact]
    public void The_data_folder_source_opens_files_below_it_and_nothing_else()
    {
        Directory.CreateDirectory(Path.Combine(_data.FullName, "Data", "meshes"));
        File.WriteAllText(Path.Combine(_data.FullName, "Data", "meshes", "x.nif"), "nif");
        File.WriteAllText(Path.Combine(_data.FullName, "outside.txt"), "secret");
        var source = new DataFolderAssetSource(Path.Combine(_data.FullName, "Data"));

        using (var handle = source.Open(@"meshes\x.nif")) Assert.NotNull(handle);
        Assert.Null(source.Open(@"meshes\missing.nif"));
        Assert.Null(source.Open("meshes"));
        Assert.Null(source.Open(@"..\outside.txt"));
    }

    [Fact]
    public void The_data_folder_source_refuses_paths_through_a_junction()
    {
        var data = Directory.CreateDirectory(Path.Combine(_data.FullName, "Data")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_data.FullName, "Elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "secret");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(data, "linked"), elsewhere])
               { RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.True(File.Exists(Path.Combine(data, "linked", "secret.txt")));
        Assert.Null(new DataFolderAssetSource(data).Open(@"linked\secret.txt"));
        Directory.Delete(Path.Combine(data, "linked")); // removes the junction, not its target
    }

    [Fact]
    public void The_session_answers_asset_requests_until_the_worker_submits()
    {
        var source = new FakeSource(@"meshes\x.nif");
        var package = TestPrograms.Package([0x4D, 0x5A], assets: ["meshes/**"]);
        byte[]? read = null;
        long? denied = -1;
        var launcher = new InProcessWorkerLauncher((i, o, nonce) =>
        {
            var channel = new FrameChannel(i, o);
            channel.Send(new Hello(ProtocolVersion.Current, nonce));
            channel.Receive<Start>();
            channel.Send(new AssetRequest(@"meshes\x.nif"));
            using (var stream = BrokeredFileSystem.FromHandle(channel.Receive<AssetReply>().Handle!.Value)())
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                read = copy.ToArray();
            }
            channel.Send(new AssetRequest(@"textures\y.dds"));
            denied = channel.Receive<AssetReply>().Handle;
            channel.Send(new Submit([[]], ""));
            channel.Receive<Result>();
            return 0;
        });

        var report = new PatchSession(launcher, new RecordingCommitter(), _ => source)
            .Run(package, new RunInputs(["run-patcher"], [], DataFolder: _data.FullName), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], read);
        Assert.Null(denied);
        Assert.Equal([@"textures\y.dds"], report.DeniedAssets);
    }

    [Fact]
    public void Layered_sources_serve_each_file_from_the_highest_one_that_has_it()
    {
        string Layer(string name, params string[] files)
        {
            var root = Directory.CreateDirectory(Path.Combine(_data.FullName, name, "meshes")).Parent!.FullName;
            foreach (var file in files) File.WriteAllText(Path.Combine(root, "meshes", file), name);
            return root;
        }
        var source = new LayeredAssetSource([new DataFolderAssetSource(Layer("High", "both.nif")), new DataFolderAssetSource(Layer("Low", "both.nif", "low.nif"))]);

        string Read(string path)
        {
            using var stream = new FileStream(source.Open(path)!, FileAccess.Read);
            return new StreamReader(stream).ReadToEnd();
        }

        Assert.Equal("High", Read(@"meshes\both.nif"));
        Assert.Equal("Low", Read(@"meshes\low.nif"));
        Assert.Null(source.Open(@"meshes\none.nif"));
    }

    [Fact]
    public void A_file_is_shared_with_the_worker_at_the_path_it_is_shared_as()
    {
        var real = Path.Combine(_data.FullName, "mods", "Some Mod", "Some.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "plugin");
        const string asData = @"C:\Game\Data\Some.esp";
        IReadOnlyList<SharedFile>? shared = null;
        var launcher = new InProcessWorkerLauncher((i, o, nonce) =>
        {
            var channel = new FrameChannel(i, o);
            channel.Send(new Hello(ProtocolVersion.Current, nonce));
            shared = channel.Receive<Start>().Files;
            channel.Send(new Submit([[]], ""));
            channel.Receive<Result>();
            return 0;
        });

        new PatchSession(launcher, new RecordingCommitter())
            .Run(TestPrograms.Package([0x4D, 0x5A]), new RunInputs(["run-patcher"], [new InputFile(real, SharedAs: asData)]), TestContext.Current.CancellationToken);

        var file = Assert.Single(shared!);
        Assert.Equal(asData, file.Path);
        Assert.NotNull(file.Handle);
    }

    public void Dispose()
    {
        try { _data.Delete(recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
