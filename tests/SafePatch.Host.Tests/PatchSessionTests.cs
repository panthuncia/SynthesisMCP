using SafePatch.Protocol;
using SafePatch.TestSupport;
using SafePatch.Worker.Core;

namespace SafePatch.Host.Tests;

/// <summary>Session mechanics with scripted workers: handshake, file sharing, failures and limits.</summary>
public sealed class PatchSessionTests : IDisposable
{
    private readonly DirectoryInfo _files = Directory.CreateTempSubdirectory("SafePatchSession-");
    private readonly RecordingCommitter _committer = new();

    private SessionReport Run(IWorkerLauncher launcher, VerifiedPackage? package = null, RunInputs? inputs = null) =>
        new PatchSession(launcher, _committer).Run(package ?? TestPrograms.Package([0x4D, 0x5A]), inputs ?? new RunInputs(["run-patcher"], []),
            TestContext.Current.CancellationToken);

    /// <summary>A worker that completes the handshake and then does <paramref name="body"/>.</summary>
    private static InProcessWorkerLauncher Scripted(Func<FrameChannel, Start, Message?> body) => new((i, o, nonce) =>
    {
        var channel = new FrameChannel(i, o);
        channel.Send(new Hello(ProtocolVersion.Current, nonce));
        if (body(channel, channel.Receive<Start>()) is { } reply)
        {
            channel.Send(reply);
            channel.Receive<Result>();
        }
        return 0;
    });

    [Fact]
    public void Shares_inline_and_handle_files_and_hands_the_output_to_the_committer()
    {
        var small = Path.Combine(_files.FullName, "plugins.txt");
        var large = Path.Combine(_files.FullName, "Skyrim.esm");
        File.WriteAllText(small, "*A.esp");
        File.WriteAllBytes(large, [1, 2, 3, 4]);

        var report = Run(Scripted((_, start) =>
        {
            // Echo what the worker could read, through the same stream factory the real worker uses.
            var inline = start.Files.Single(f => f.Path == small);
            var shared = start.Files.Single(f => f.Path == large);
            using var stream = BrokeredFileSystem.FromHandle(shared.Handle!.Value)();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return new Submit([[.. inline.Contents!, .. copy.ToArray()]], "hello from the worker");
        }), inputs: new RunInputs(["run-patcher"], [new InputFile(small, Inline: true), new InputFile(large)]));

        Assert.Equal([.. "*A.esp"u8.ToArray(), 1, 2, 3, 4], _committer.OutputPlugin);
        Assert.Equal("hello from the worker", report.Log);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PatchSession.MaxOutputParts + 1)]
    public void An_output_with_no_parts_or_too_many_is_rejected(int parts)
    {
        var e = Assert.Throws<PatchRejectedException>(() =>
            Run(Scripted((_, _) => new Submit([.. Enumerable.Range(0, parts).Select(_ => new byte[] { 1 })], "log"))));
        Assert.Contains("plugin parts", e.Message);
        Assert.Null(_committer.OutputPlugins);
    }

    [Fact]
    public void Split_parts_reach_the_committer_in_order()
    {
        Run(Scripted((_, _) => new Submit([[1], [2], [3]], "log")));
        Assert.Equal([[1], [2], [3]], _committer.OutputPlugins!);
    }

    [Fact]
    public void A_failed_program_is_rejected_with_its_log()
    {
        var e = Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) => new Failed("InvalidOperationException: halfway", "some output"))));
        Assert.Contains("halfway", e.Message);
        Assert.Equal("some output", e.Log);
        Assert.Null(_committer.OutputPlugin);
    }

    [Fact]
    public void A_committer_rejection_fails_the_run_and_tells_the_worker()
    {
        Result? told = null;
        var launcher = new InProcessWorkerLauncher((i, o, nonce) =>
        {
            var channel = new FrameChannel(i, o);
            channel.Send(new Hello(ProtocolVersion.Current, nonce));
            channel.Receive<Start>();
            channel.Send(new Submit([[1]], "log"));
            told = channel.Receive<Result>();
            return 0;
        });
        var rejecting = new RejectingCommitter();

        var e = Assert.Throws<PatchRejectedException>(() =>
            new PatchSession(launcher, rejecting).Run(TestPrograms.Package([0x4D, 0x5A]), new RunInputs([], []), TestContext.Current.CancellationToken));

        Assert.Equal("no thanks", e.Message);
        Assert.Equal("log", e.Log);
        SpinWait.SpinUntil(() => told is not null, TimeSpan.FromSeconds(5));
        Assert.False(told!.Accepted);
    }

    [Fact]
    public void A_worker_with_the_wrong_nonce_is_rejected()
    {
        var launcher = new InProcessWorkerLauncher((i, o, _) =>
        {
            new FrameChannel(i, o).Send(new Hello(ProtocolVersion.Current, "guess"));
            return 0;
        });
        var e = Assert.Throws<PatchRejectedException>(() => Run(launcher));
        Assert.Contains("nonce", e.Message);
    }

    [Fact]
    public void A_worker_from_another_protocol_version_is_rejected_naming_both()
    {
        var launcher = new InProcessWorkerLauncher((i, o, nonce) =>
        {
            new FrameChannel(i, o).Send(new Hello(ProtocolVersion.Current + 1, nonce));
            return 0;
        });

        var e = Assert.Throws<PatchRejectedException>(() => Run(launcher));

        Assert.Contains($"protocol {ProtocolVersion.Current + 1}, expected {ProtocolVersion.Current}", e.Message);
    }

    [Fact]
    public void A_worker_sending_garbage_is_rejected()
    {
        var launcher = new InProcessWorkerLauncher((_, o, _) =>
        {
            o.Write([5, 0, 0, 0, 1, 2, 3, 4, 5]);
            return 0;
        });
        Assert.Throws<PatchRejectedException>(() => Run(launcher));
    }

    [Fact]
    public void A_worker_that_exits_early_is_rejected()
    {
        // Depending on timing the host sees a broken pipe or end-of-stream; both must reject.
        Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) => null)));
        Assert.Null(_committer.OutputPlugin);
    }

    [Fact]
    public void A_worker_that_stalls_is_killed_at_the_timeout()
    {
        var package = TestPrograms.Package([0x4D, 0x5A], policy: PatchPolicy.PublisherDefault with { Timeout = TimeSpan.FromMilliseconds(300) });
        var e = Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) =>
        {
            Thread.Sleep(Timeout.Infinite);
            return null;
        }), package));
        Assert.Contains("timed out", e.Message);
    }

    [Fact]
    public void Inline_files_over_the_limit_are_refused()
    {
        var big = Path.Combine(_files.FullName, "big.txt");
        File.WriteAllBytes(big, new byte[1024]);
        var package = TestPrograms.Package([0x4D, 0x5A], policy: PatchPolicy.PublisherDefault with { MaxInlineFileBytes = 100 });

        Assert.Throws<SafePatchException>(() => Run(Scripted((_, _) => null), package, new RunInputs([], [new InputFile(big, Inline: true)])));
    }

    [Fact]
    public void An_oversized_log_is_truncated()
    {
        var package = TestPrograms.Package([0x4D, 0x5A], policy: PatchPolicy.PublisherDefault with { MaxLogChars = 10 });

        var report = Run(Scripted((_, _) => new Submit([[1]], new string('x', 1_000_000))), package);

        Assert.Equal(new string('x', 10) + "…", report.Log);
    }

    [Fact]
    public void An_oversized_persistence_file_is_rejected()
    {
        var package = TestPrograms.Package([0x4D, 0x5A], policy: PatchPolicy.PublisherDefault with { MaxInlineFileBytes = 100 });

        var e = Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) => new Submit([[1]], "", Persistence: new byte[101])), package));

        Assert.Contains("persistence", e.Message);
        Assert.Null(_committer.OutputPlugins);
    }

    public static TheoryData<Message> UnexpectedMessages() =>
    [
        new Hello(ProtocolVersion.Current, "again"),
        new Start([1], [], []),
        new AssetReply(1),
        new Result(true, []),
    ];

    [Theory]
    [MemberData(nameof(UnexpectedMessages))]
    public void An_unexpected_message_mid_session_is_rejected(Message message)
    {
        var e = Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) => message)));

        Assert.Contains($"Unexpected {message.GetType().Name}", e.Message);
        Assert.Null(_committer.OutputPlugins);
    }

    [Fact]
    public void A_null_output_part_is_rejected()
    {
        var e = Assert.Throws<PatchRejectedException>(() => Run(Scripted((_, _) => new Submit([[1], null!], ""))));

        Assert.Contains("plugin parts", e.Message);
        Assert.Null(_committer.OutputPlugins);
    }

    private sealed class RejectingCommitter : IPatchCommitter
    {
        public IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy) => throw new PatchRejectedException("no thanks");
    }

    public void Dispose() => _files.Delete(recursive: true);
}
