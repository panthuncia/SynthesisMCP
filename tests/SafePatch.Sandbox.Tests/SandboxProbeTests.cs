using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using SafePatch.Host;
using SafePatch.Sandbox.Windows;
using SafePatch.TestSupport;

namespace SafePatch.Sandbox.Tests;

/// <summary>
/// Runs native patch functions inside the real AppContainer worker, through the real Synthesis
/// pipeline over brokered plugin handles. The probe tries each escape and reports whether it was
/// allowed; every one must be denied.
/// </summary>
[Trait("Category", "Sandbox")]
[SupportedOSPlatform("windows")]
public sealed class SandboxProbeTests : IDisposable
{
    private readonly DirectoryInfo _userDir = Directory.CreateTempSubdirectory("SafePatchProbe-");
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly HostRun _run = new();

    private string ProbeSource => $$"""
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Net.Sockets;
        using Microsoft.Win32;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class Probe
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                void Try(string name, Action action)
                {
                    try { action(); Console.WriteLine("ALLOWED " + name); }
                    catch (Exception e) { Console.WriteLine("denied " + name + ": " + e.GetType().Name); }
                }

                Try("read-user-file", () => File.ReadAllText(@"{{Path.Combine(_userDir.FullName, "secret.txt")}}"));
                Try("list-user-dir", () => Directory.GetFiles(@"{{_userDir.FullName}}"));
                Try("write-user-dir", () => File.WriteAllText(@"{{Path.Combine(_userDir.FullName, "escaped.txt")}}", "x"));
                Try("write-worker-dir", () => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "escaped.txt"), "x"));
                Try("read-real-plugin-path", () => File.ReadAllBytes(@"{{Path.Combine(_run.DataFolder, "Skyrim.esm")}}"));
                // A blocked connect is dropped, not refused, so cap the wait; the test also checks the listener.
                Try("connect-loopback", () => { using var c = new TcpClient(); if (!c.ConnectAsync("127.0.0.1", {{((IPEndPoint)_listener.LocalEndpoint).Port}}).Wait(2000)) throw new TimeoutException(); });
                Try("start-process", () => Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false })!.WaitForExit());
                Try("write-registry", () => { using var k = Registry.CurrentUser.CreateSubKey(@"Software\SafePatchProbe"); k.SetValue("x", 1); });
                Try("see-user-profile-variable", () => { if (Environment.GetEnvironmentVariable("USERPROFILE") == null) throw new InvalidOperationException(); });
                // The brokered view of the same plugin is readable: the pipeline loaded it.
                Console.WriteLine("brokered-plugins " + state.LoadOrder.Count);
            }
        }
        """;

    private static AppContainerLauncher Launcher(ulong memoryLimit = 2UL << 30) => new(new SandboxOptions
    {
        WorkerPath = SafePatch.Synthesis.SafePatchHost.WorkerPath,
        MemoryLimitBytes = memoryLimit,
    });

    public SandboxProbeTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The sandbox is Windows-only.");
        _listener.Start();
        File.WriteAllText(Path.Combine(_userDir.FullName, "secret.txt"), "secret");
    }

    [Fact]
    public void Every_escape_attempt_is_denied_while_brokered_plugins_load()
    {
        var report = _run.Run(ProbeSource, launcher: Launcher());

        var lines = report.Log.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(9, lines.Count(l => l.StartsWith("denied ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("ALLOWED ", StringComparison.Ordinal));
        Assert.Contains("brokered-plugins 4", lines); // Skyrim.esm, CACO, Other, and the patch itself
        Assert.False(File.Exists(Path.Combine(_userDir.FullName, "escaped.txt")));
        Assert.False(_listener.Pending());
    }

    [Fact]
    public void Assets_are_readable_only_through_the_broker_and_only_where_the_manifest_allows()
    {
        foreach (var (path, text) in new[] { (@"meshes\safepatch\ok.nif", "NIF"), (@"textures\safepatch\secret.dds", "DDS") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_run.DataFolder, path))!);
            File.WriteAllText(Path.Combine(_run.DataFolder, path), text);
        }
        // An archive no plugin is named after, listed only in the game INI.
        BsaFixture.Write(Path.Combine(_run.DataFolder, "Custom - Packed.bsa"), new Dictionary<string, byte[]> { [@"meshes\safepatch\packed.nif"] = "BSA"u8.ToArray() });
        File.WriteAllLines(_run.GameIni, ["[Archive]", "sResourceArchiveList=Custom - Packed.bsa"]);
        var program = $$"""
            using System;
            using System.IO;
            using Mutagen.Bethesda.Assets;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;

            public static class Assets
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    foreach (var path in new[] { @"meshes\safepatch\ok.nif", @"textures\safepatch\secret.dds", @"meshes\safepatch\packed.nif" })
                    {
                        Console.WriteLine(state.AssetProvider.TryGetStream(new DataRelativePath(path), out var stream)
                            ? "read " + path + " " + new StreamReader(stream).ReadToEnd()
                            : "missing " + path);
                    }
                    try { File.ReadAllText(@"{{Path.Combine(_run.DataFolder, @"textures\safepatch\secret.dds")}}"); Console.WriteLine("ALLOWED direct"); }
                    catch (Exception e) { Console.WriteLine("denied direct: " + e.GetType().Name); }
                    // Mutagen looks for the game INI (which lists archives no plugin names) under My Documents.
                    Console.WriteLine("documents " + Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                }
            }
            """;

        var report = _run.Run(program, launcher: Launcher(), assets: ["meshes/**"]);

        var lines = report.Log.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(@"read meshes\safepatch\ok.nif NIF", lines);
        Assert.Contains(@"missing textures\safepatch\secret.dds", lines);
        Assert.Contains(@"read meshes\safepatch\packed.nif BSA", lines);
        Assert.Contains(lines, l => l.StartsWith("denied direct", StringComparison.Ordinal));
        // The container cannot resolve My Documents, so Mutagen cannot find the game INI itself. The host
        // shares it, and the INI-listed archive above is read through it (ADR 006).
        Assert.Equal("documents", lines.Single(l => l.StartsWith("documents", StringComparison.Ordinal)));
        Assert.Equal([@"textures\safepatch\secret.dds"], report.DeniedAssets);
    }

    [Fact]
    public void The_native_merge_program_runs_in_the_sandbox()
    {
        var report = _run.Run(SamplePrograms.LeveledListMerge, launcher: Launcher());

        Assert.Equal(["Entries"], Assert.Single(report.Changes).Fields);
        Assert.Contains("Restored 1 entries to LItemSafePatchTest", report.Log);
        Assert.Contains(_run.Plugins.CacoPotion, Assert.Single(_run.PatchMod.LeveledItems).Entries!.Select(e => e.Data!.Reference.FormKey));
    }

    [Fact]
    public void A_runaway_loop_is_killed_at_the_timeout()
    {
        const string spin = """
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Spin { public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) { while (true) { } } }
            """;
        var package = TestPrograms.Package(TestPrograms.Compile(spin), policy: PatchPolicy.PublisherDefault with { Timeout = TimeSpan.FromSeconds(10) });

        var e = Assert.Throws<PatchRejectedException>(() =>
            new PatchSession(Launcher(), _run.Committer()).Run(package, _run.Inputs(), TestContext.Current.CancellationToken));
        Assert.Contains("timed out", e.Message);
    }

    [Fact]
    public void Cancelling_the_run_kills_the_worker_and_leaves_the_patch_untouched()
    {
        const string spin = """
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Spin
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    state.PatchMod.LeveledItems.AddNew("LItemNeverCommitted");
                    while (true) { }
                }
            }
            """;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(3));
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => new PatchSession(Launcher(), _run.Committer())
            .Run(TestPrograms.Package(TestPrograms.Compile(spin), creatable: ["LeveledItem"]), _run.Inputs(), cancel.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"Cancellation took {started.Elapsed}.");
        Assert.Empty(_run.PatchMod.LeveledItems);
    }

    [Fact]
    public void The_same_program_on_the_same_inputs_produces_the_same_bytes_in_and_out_of_the_sandbox()
    {
        var package = TestPrograms.Package(TestPrograms.Compile(SamplePrograms.LeveledListMerge));
        byte[] Output(IWorkerLauncher launcher)
        {
            var recorder = new RecordingCommitter();
            new PatchSession(launcher, recorder).Run(package, _run.Inputs(), TestContext.Current.CancellationToken);
            return Assert.Single(recorder.OutputPlugins!);
        }

        var sandboxed = Output(Launcher());

        Assert.Equal(sandboxed, Output(Launcher()));
        Assert.Equal(sandboxed, Output(InProcessWorkerLauncher.Real()));
    }

    [Fact]
    public void Exceeding_the_memory_limit_kills_the_worker()
    {
        const string hog = """
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Hog
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    var keep = new System.Collections.Generic.List<byte[]>();
                    while (true) { var b = new byte[16 << 20]; System.Array.Fill(b, (byte)1); keep.Add(b); }
                }
            }
            """;

        Assert.Throws<PatchRejectedException>(() => _run.Run(hog, launcher: Launcher(memoryLimit: 512UL << 20)));
        Assert.Empty(_run.PatchMod.LeveledItems);
    }

    [Fact]
    public void New_records_keep_their_FormKeys_across_runs_in_the_sandbox()
    {
        // The persistence folder is the host's, which the AppContainer can't see; the worker sees its brokered copy.
        var persistence = Path.Combine(_userDir.FullName, "Persistence");
        static string Create(params string[] editorIds) => $$"""
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class P
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    {{string.Concat(editorIds.Select(e => $"state.PatchMod.LeveledItems.AddNew(\"{e}\");"))}}
                }
            }
            """;
        global::Mutagen.Bethesda.Plugins.FormKey first;
        using (var run = new HostRun(persistenceFolder: persistence))
        {
            run.Run(Create("LItemPersisted"), creatable: ["LeveledItem"], launcher: Launcher());
            first = Assert.Single(run.PatchMod.LeveledItems).FormKey;
        }

        using var again = new HostRun(persistenceFolder: persistence);
        again.Run(Create("LItemNewcomer", "LItemPersisted"), creatable: ["LeveledItem"], launcher: Launcher());

        Assert.Equal(first, again.PatchMod.LeveledItems.Single(l => l.EditorID == "LItemPersisted").FormKey);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
        _run.Dispose();
        _userDir.Delete(recursive: true);
    }
}
