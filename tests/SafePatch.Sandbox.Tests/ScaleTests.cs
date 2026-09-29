using System.Diagnostics;
using System.Runtime.Versioning;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.Sandbox.Windows;
using SafePatch.TestSupport;

namespace SafePatch.Sandbox.Tests;

/// <summary>
/// Runs a merge over a synthetic load order of hundreds of plugins in the real sandbox, and reports
/// where the time and memory go. Run manually (<c>--filter Category=Scale</c>); the numbers are recorded
/// in the implementation plan's status.
/// </summary>
[Trait("Category", "Scale")]
[SupportedOSPlatform("windows")]
public sealed class ScaleTests
{
    private static AppContainerLauncher Launcher() => new(new SandboxOptions { WorkerPath = SafePatch.Synthesis.SafePatchHost.WorkerPath });

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact]
    public void A_merge_over_hundreds_of_plugins_completes_within_the_default_limits()
    {
        var fixture = ScaleFixture.Default;
        var clock = Stopwatch.StartNew();
        using var run = new HostRun(extraArguments: ["--SplitIfMaxMastersExceeded"], extraPlugins: fixture.Build());
        Report($"fixture: {fixture} written in {clock.Elapsed.TotalSeconds:F1} s");

        var launcher = Launcher();
        var committer = new MeasuringCommitter(run.Committer());
        var package = TestPrograms.Package(TestPrograms.Compile(ScaleFixture.MergeProgram), ScaleFixture.MergeWritable, maxRecords: 100_000);
        clock.Restart();
        var report = new PatchSession(launcher, committer).Run(package, run.Inputs(), TestContext.Current.CancellationToken);
        var total = clock.Elapsed;

        Report($"session: {total.TotalSeconds:F1} s, of which host commit {committer.Elapsed.TotalSeconds:F1} s");
        Report($"worker peak memory: {launcher.LastPeakMemoryBytes / (1024 * 1024)} MiB");
        Report($"output: {committer.Parts} part(s), {committer.Bytes / 1024} KiB");
        Report($"changes: {report.Changes.Count}");
        Report(report.Log.Trim());

        Assert.Equal(fixture.References, report.Changes.Count(c => c.RecordType == "PlacedObject"));
        Assert.True(report.Changes.Count(c => c.RecordType == "LeveledItem") > fixture.Lists / 2, $"{report.Changes.Count} changes");
        Assert.True(committer.Parts > 1, "A merge over this many plugins needs more masters than one plugin holds.");
        Assert.All(run.PatchMod.LeveledItems.Where(l => l.EditorID!.StartsWith("ScaleList")), l => Assert.True(l.Entries!.Count <= 255));
        Assert.All(run.PatchMod.EnumerateMajorRecords<IPlacedObjectGetter>(), p => Assert.Equal(1.5f, p.Scale));
    }

    [Fact]
    public void Loose_asset_requests_cost_one_round_trip_each()
    {
        const int assets = 2000;
        using var run = new HostRun();
        var folder = Path.Combine(run.DataFolder, "meshes", "scale");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < assets; i++) File.WriteAllText(Path.Combine(folder, $"m{i}.nif"), "NIF");
        var program = $$"""
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Assets;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Probe
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    var found = 0;
                    for (var i = 0; i < {{assets}}; i++)
                        if (state.AssetProvider.TryGetSize(new DataRelativePath($@"meshes\scale\m{i}.nif"), out _)) found++;
                    state.PatchMod.LeveledItems.AddNew("LItemFound" + found);
                }
            }
            """;
        var package = TestPrograms.Package(TestPrograms.Compile(program), [], ["LeveledItem"], assets: ["meshes/scale/*.nif"]);

        var clock = Stopwatch.StartNew();
        var report = new PatchSession(Launcher(), run.Committer()).Run(package, run.Inputs(), TestContext.Current.CancellationToken);
        var total = clock.Elapsed;

        Report($"{assets} asset requests: session {total.TotalSeconds:F1} s");
        Assert.Equal($"LItemFound{assets}", Assert.Single(report.Changes).EditorId);
    }

    /// <summary>Times the commit and measures what the worker returned.</summary>
    private sealed class MeasuringCommitter(IPatchCommitter inner) : IPatchCommitter
    {
        public TimeSpan Elapsed { get; private set; }
        public int Parts { get; private set; }
        public long Bytes { get; private set; }

        public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy)
        {
            (Parts, Bytes) = (outputPlugins.Count, outputPlugins.Sum(p => (long)p.Length));
            var clock = Stopwatch.StartNew();
            try { return inner.Commit(outputPlugins, persistence, policy); }
            finally { Elapsed = clock.Elapsed; }
        }
    }
}
