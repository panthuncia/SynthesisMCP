using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Mutagen.Tests;

/// <summary>
/// The worker's plugin is untrusted bytes. Feeds the committer mutated versions of a real output:
/// each must be applied as a valid patch or rejected with <see cref="PatchRejectedException"/>, and
/// a rejection must leave the patch mod exactly as it was. Seeded; set <c>SAFEPATCH_FUZZ_ITERATIONS</c>
/// to run longer.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class MalformedOutputTests : IDisposable
{
    private readonly HostRun _run = new();

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("SAFEPATCH_FUZZ_ITERATIONS"), out var n) && n > 0 ? Math.Max(1, n / 100) : 200;

    [Fact]
    public async Task Mutated_plugins_are_applied_whole_or_rejected_without_touching_the_patch()
    {
        var recorder = new RecordingCommitter();
        var package = TestPrograms.Package(TestPrograms.Compile(SamplePrograms.LeveledListMerge));
        new PatchSession(InProcessWorkerLauncher.Real(), recorder).Run(package, _run.Inputs(), TestContext.Current.CancellationToken);
        var original = recorder.OutputPlugin!;

        var random = new Random(20260929);
        var outcomes = new Dictionary<string, int>();
        for (var i = 0; i < Iterations; i++)
        {
            var mutated = Mutate(random, original);
            var before = Snapshot();
            // The commit runs in the host, so a hang is a denial of service: fail with the input rather than stall.
            var commit = Task.Run(() => _run.Committer().Commit([mutated], null, package.Policy), TestContext.Current.CancellationToken);
            try
            {
                await commit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Count(outcomes, "accepted");
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Iteration {i}: the commit did not finish for input {Convert.ToBase64String(mutated)}");
            }
            catch (PatchRejectedException)
            {
                Assert.True(before.SequenceEqual(Snapshot()), $"Iteration {i}: a rejected plugin changed the patch mod.");
                Count(outcomes, "rejected");
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {i}: {e.GetType().Name} escaped the committer for input {Convert.ToBase64String(mutated)}\n{e}");
            }
        }
        Assert.True(outcomes.GetValueOrDefault("rejected") > 0, string.Join(", ", outcomes));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'T', (byte)'E', (byte)'S', (byte)'4', 0xFF, 0xFF, 0xFF, 0x7F })]
    public void Garbage_is_rejected(byte[] plugin)
    {
        var e = Assert.Throws<PatchRejectedException>(() =>
            _run.Committer().Commit([plugin], null, TestPrograms.Package([0x4D, 0x5A]).Policy));

        Assert.Contains("The worker's plugin", e.Message);
        Assert.Empty(_run.PatchMod.LeveledItems);
    }

    public static TheoryData<string, int> BadFraming() => new()
    {
        { "zero-length group", 0 },          // Mutagen's reader never advances past it
        { "group shorter than its header", 10 },
        { "group longer than the plugin", int.MaxValue },
    };

    [Theory]
    [MemberData(nameof(BadFraming))]
    public void A_badly_framed_group_is_rejected_before_Mutagen_reads_it(string description, int groupLength)
    {
        var output = RealOutput();
        var afterHeader = 24 + BitConverter.ToInt32(output, 4);
        var group = new byte[24];
        "GRUP"u8.CopyTo(group);
        BitConverter.TryWriteBytes(group.AsSpan(4), groupLength);
        byte[] plugin = [.. output[..afterHeader], .. group, .. output[afterHeader..]];

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<PatchRejectedException>(() => _run.Committer().Commit([plugin], null, TestPrograms.Package([0x4D, 0x5A]).Policy));

        Assert.Contains("malformed", e.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), description);
        Assert.Empty(_run.PatchMod.LeveledItems);
    }

    [Fact]
    public void A_compressed_record_claiming_a_huge_size_is_rejected()
    {
        var output = RealOutput();
        // The first record after the header group's 24-byte header: set its compressed flag and a huge inflated size.
        var record = 24 + BitConverter.ToInt32(output, 4) + 24;
        var flags = BitConverter.ToUInt32(output, record + 8) | 0x0004_0000;
        BitConverter.TryWriteBytes(output.AsSpan(record + 8), flags);
        BitConverter.TryWriteBytes(output.AsSpan(record + 24), uint.MaxValue);

        var e = Assert.Throws<PatchRejectedException>(() => _run.Committer().Commit([output], null, TestPrograms.Package([0x4D, 0x5A]).Policy));

        Assert.Contains("compressed record", e.Message);
    }

    private byte[] RealOutput()
    {
        var recorder = new RecordingCommitter();
        new PatchSession(InProcessWorkerLauncher.Real(), recorder).Run(
            TestPrograms.Package(TestPrograms.Compile(SamplePrograms.LeveledListMerge)), _run.Inputs(), TestContext.Current.CancellationToken);
        return recorder.OutputPlugin!;
    }

    /// <summary>The patch mod as bytes, to compare before and after a rejected commit.</summary>
    private byte[] Snapshot()
    {
        var path = Path.Combine(_run.Root, "Snapshot", "Synthesis.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _run.PatchMod.WriteToBinary(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] Mutate(Random random, byte[] original)
    {
        var bytes = (byte[])original.Clone();
        switch (random.Next(4))
        {
            case 0: // flip a few bits
                for (var n = random.Next(1, 4); n > 0; n--) bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                return bytes;
            case 1: // overwrite a 32-bit value with an extreme one (lengths, counts, FormIDs)
                var at = random.Next(bytes.Length - 4);
                BitConverter.TryWriteBytes(bytes.AsSpan(at), random.Next(3) switch { 0 => 0, 1 => -1, _ => random.Next() });
                return bytes;
            case 2: // truncate
                return bytes[..random.Next(bytes.Length)];
            default: // duplicate a run
                var start = random.Next(bytes.Length);
                var length = Math.Min(random.Next(1, 64), bytes.Length - start);
                return [.. bytes[..(start + length)], .. bytes[start..]];
        }
    }

    private static void Count(Dictionary<string, int> outcomes, string outcome) => outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

    public void Dispose() => _run.Dispose();
}
