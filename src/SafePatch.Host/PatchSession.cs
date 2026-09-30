using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SafePatch.Protocol;

namespace SafePatch.Host;

/// <param name="SettingsSha256">Hash of the settings file the program ran with, if any.</param>
/// <param name="DeniedAssets">Assets the program asked for that the manifest does not allow (the first few).</param>
public sealed record SessionReport(
    string PackageName, string ProgramSha256, IReadOnlyList<RecordChange> Changes, string Log,
    string? SettingsSha256 = null, IReadOnlyList<string>? DeniedAssets = null)
{
    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions(FrameChannel.JsonOptions) { WriteIndented = true });
}

/// <summary>Raised when a run is rejected. Carries the program's log when there is one.</summary>
public sealed class PatchRejectedException(string message, string? log = null, Exception? inner = null)
    : SafePatchException(message, inner)
{
    public string? Log { get; } = log;
}

/// <summary>
/// Runs one package: shares the input files read-only, launches the worker, serves its asset
/// requests, receives the plugin it produced, and hands it to the committer, which validates it
/// before touching the real patch. Any failure throws, so the caller (and Synthesis) stops.
/// </summary>
/// <param name="assets">Opens the Data folder's loose files; defaults to the folder on disk.</param>
public sealed class PatchSession(IWorkerLauncher launcher, IPatchCommitter committer, Func<string, IAssetSource>? assets = null)
{
    /// <summary>Each part holds up to 254 masters, so this covers load orders far beyond the game's limits.</summary>
    public const int MaxOutputParts = 64;

    public SessionReport Run(VerifiedPackage package, RunInputs inputs, CancellationToken cancel = default)
    {
        var policy = package.Policy;
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var openFiles = new List<SafeFileHandle>();

        using var worker = launcher.Launch(nonce);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(policy.Timeout);
        using var killOnTimeout = timeout.Token.Register(worker.Kill);

        Submit submit;
        string? settingsSha256 = null;
        AssetBroker? broker = null;
        var channel = new FrameChannel(worker.FromWorker, worker.ToWorker);
        try
        {
            var hello = channel.Receive<Hello>();
            if (hello.ProtocolVersion != ProtocolVersion.Current)
                throw new PatchRejectedException($"Worker speaks protocol {hello.ProtocolVersion}, expected {ProtocolVersion.Current}.");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Nonce), Encoding.UTF8.GetBytes(nonce)))
                throw new PatchRejectedException("Worker did not present the run nonce.");

            var files = Share(inputs.Files, worker, openFiles, policy);
            if (inputs.SettingsFile is { } settingsFile)
            {
                var contents = files.Single(f => f.Path == settingsFile).Contents
                               ?? throw new SafePatchException("The settings file must be copied to the worker, not shared.");
                settingsSha256 = Convert.ToHexStringLower(SHA256.HashData(contents));
            }
            if (inputs.GameIniPath is { } ini && files.All(f => f.Path != ini || f.Contents is null))
                throw new SafePatchException("The game INI must be copied to the worker.");
            channel.Send(new Start(package.Program, inputs.Arguments, files, package.Manifest.Settings?.Path, inputs.GameIniPath));

            var message = channel.Receive();
            while (message is AssetRequest request)
            {
                broker ??= new AssetBroker(
                    inputs.DataFolder is { } data ? (assets ?? (d => new DataFolderAssetSource(d)))(data) : new NoAssets(), policy);
                var handle = broker.Open(request.Path);
                if (handle is not null) openFiles.Add(handle);
                channel.Send(new AssetReply(handle is null ? null : worker.ShareReadOnly(handle)));
                message = channel.Receive();
            }

            submit = message switch
            {
                Submit s => s,
                Failed f => throw new PatchRejectedException($"Patch program failed: {f.Error}", Truncate(f.Log, policy.MaxLogChars)),
                null => throw new PatchRejectedException($"Worker exited before submitting a patch ({worker.DescribeExit() ?? "no exit information"})."),
                var other => throw new PatchRejectedException($"Unexpected {other.GetType().Name} from worker."),
            };
        }
        catch (Exception e) when (timeout.IsCancellationRequested && e is not OperationCanceledException)
        {
            cancel.ThrowIfCancellationRequested();
            throw new PatchRejectedException($"Worker timed out after {policy.Timeout}.", inner: e);
        }
        catch (Exception e) when (e is ProtocolException or IOException)
        {
            throw new PatchRejectedException($"Worker channel failed: {e.Message} ({worker.DescribeExit() ?? "no exit information"})", inner: e);
        }
        finally
        {
            foreach (var file in openFiles) file.Dispose();
        }

        var log = Truncate(submit.Log, policy.MaxLogChars);
        IReadOnlyList<RecordChange> changes;
        try
        {
            if (submit.Persistence?.Length > policy.MaxInlineFileBytes)
                throw new PatchRejectedException("The worker returned an oversized FormKey persistence file.");
            if (submit.OutputPlugins is not { Count: > 0 and <= MaxOutputParts } || submit.OutputPlugins.Any(p => p is null))
                throw new PatchRejectedException($"The worker must return between 1 and {MaxOutputParts} plugin parts.");
            changes = committer.Commit(submit.OutputPlugins, submit.Persistence, policy);
        }
        catch (PatchRejectedException e)
        {
            TrySend(channel, new Result(false, [e.Message]));
            throw new PatchRejectedException(e.Message, log, e);
        }
        TrySend(channel, new Result(true, []));
        return new SessionReport(package.Manifest.Name, package.Manifest.ProgramSha256, changes, log, settingsSha256, broker?.Denied ?? []);
    }

    private static List<SharedFile> Share(IReadOnlyList<InputFile> files, IWorkerProcess worker, List<SafeFileHandle> openFiles, PatchPolicy policy)
    {
        var shared = new List<SharedFile>(files.Count);
        foreach (var file in files)
        {
            if (file.Inline)
            {
                var info = new FileInfo(file.Path);
                if (info.Length > policy.MaxInlineFileBytes) throw new SafePatchException($"{file.Path} is too large to copy to the worker.");
                shared.Add(new SharedFile(file.WorkerPath, Contents: File.ReadAllBytes(file.Path)));
            }
            else
            {
                var handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                openFiles.Add(handle);
                shared.Add(new SharedFile(file.WorkerPath, Handle: worker.ShareReadOnly(handle)));
            }
        }
        return shared;
    }

    private static void TrySend(FrameChannel channel, Message message)
    {
        // The worker's view of the outcome is informational; a vanished worker must not mask the result.
        try { channel.Send(message); }
        catch (IOException) { }
    }

    private sealed class NoAssets : IAssetSource
    {
        public SafeFileHandle? Open(string dataRelativePath) => null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
