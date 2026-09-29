using CommandLine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Synthesis.CLI;
using SafePatch.Protocol;

namespace SafePatch.Worker.Core;

/// <summary>The worker side of a session. Runs inside the sandbox.</summary>
public static class WorkerRuntime
{
    /// <param name="openHandle">Turns a shared handle value into a readable stream.</param>
    /// <param name="log">Where the program's console output was redirected, if anywhere.</param>
    /// <returns>0 if the host accepted the output, 1 otherwise.</returns>
    public static int Run(Stream fromHost, Stream toHost, string nonce, Func<long, Func<Stream>> openHandle, StringWriter? log = null)
    {
        var channel = new FrameChannel(fromHost, toHost);
        channel.Send(new Hello(ProtocolVersion.Current, nonce));
        var start = channel.Receive<Start>();

        // The pipeline may read assets from any thread; one request/reply at a time on the channel.
        var channelLock = new Lock();
        long? RequestAsset(string dataRelativePath)
        {
            lock (channelLock)
            {
                channel.Send(new AssetRequest(dataRelativePath));
                return channel.Receive<AssetReply>().Handle;
            }
        }

        try
        {
            var output = RunPipeline(start, openHandle, RequestAsset);
            channel.Send(new Submit(output.Plugins, log?.ToString() ?? "", output.Persistence));
        }
        catch (Exception e) when (e is not ProtocolException and not IOException)
        {
            channel.Send(new Failed($"{e.GetType().Name}: {e.Message}", log?.ToString() ?? ""));
        }

        return channel.Receive<Result>().Accepted ? 0 : 1;
    }

    /// <summary>
    /// What the pipeline wrote: the output plugin (then its split parts, if Synthesis split it) and,
    /// when the run uses persistence, the patcher's FormKey allocation file.
    /// </summary>
    public sealed record PipelineOutput(IReadOnlyList<byte[]> Plugins, byte[]? Persistence);

    /// <summary>Runs the program through the real Synthesis pipeline and returns what it wrote.</summary>
    /// <param name="requestAsset">Asks the host for a loose Data file; returns its shared handle, or null.</param>
    public static PipelineOutput RunPipeline(Start start, Func<long, Func<Stream>> openHandle, Func<string, long?>? requestAsset = null)
    {
        var run = ParseRun(start.Arguments);
        // The output is an intermediate the host re-reads; the host's own Synthesis run localizes the real one.
        if (run.Localize) throw new ArgumentException("The worker's output must not be localized.");
        var fileSystem = new BrokeredFileSystem();
        fileSystem.AddDirectory(run.DataFolderPath);
        if (requestAsset is not null)
            fileSystem.SetAssetResolver(run.DataFolderPath, path =>
                !SharedUpFront(path) && requestAsset(path) is { } handle ? openHandle(handle) : null);
        foreach (var file in start.Files)
        {
            if (file is { Handle: { } handle }) fileSystem.AddBrokered(file.Path, openHandle(handle));
            else fileSystem.AddFile(file.Path, new(file.Contents ?? []));
        }

        var program = PatchProgram.Load(start.Program);
        var pipeline = SynthesisPipeline.Instance.AddPatch<ISkyrimMod, ISkyrimModGetter>(async state =>
        {
            await program.Run(start.GameIniPath is { } ini ? IniArchives.WithIniArchives(state, run, fileSystem, ini) : state);
            // A localized source plugin makes the patch localized; keep the intermediate's strings embedded.
            if (state.PatchMod.CanUseLocalization) state.PatchMod.UsingLocalization = false;
        });
        if (start.SettingsPath is { } settingsPath) BindSettings(pipeline, program, settingsPath);
        var exitCode = pipeline.Run([.. start.Arguments], fileSystem).GetAwaiter().GetResult();
        if (exitCode != 0) throw new InvalidOperationException($"The Synthesis pipeline failed with exit code {exitCode}.");

        if (!fileSystem.File.Exists(run.OutputPath)) throw new InvalidOperationException("The Synthesis pipeline wrote no output plugin.");

        // Synthesis's allocator commits "<PatcherName>.txt" when the pipeline disposes its state.
        byte[]? persistence = null;
        if (run.PersistencePath is { } folder && run.PatcherName is { } name)
        {
            var file = Path.Combine(folder, name + ".txt");
            if (fileSystem.File.Exists(file)) persistence = fileSystem.File.ReadAllBytes(file);
        }
        // With --SplitIfMaxMastersExceeded, a patch needing too many masters is written as the plugin plus _2, _3...
        var output = new ModPath(ModKey.FromFileName(Path.GetFileName(run.OutputPath)), run.OutputPath);
        var parts = MultiModFileAnalysis.GetSplitModFiles(output, fileSystem) is { Count: > 0 } split ? split.Select(p => p.Path).ToList() : [run.OutputPath];
        return new PipelineOutput([.. parts.Select(fileSystem.File.ReadAllBytes)], persistence);
    }

    /// <summary>
    /// What a native patcher's Main does: <c>SetAutogeneratedSettings(name, path, out Program.Settings)</c>.
    /// Synthesis then reads the settings from the extra data folder in the brokered file system.
    /// </summary>
    private static void BindSettings(SynthesisPipeline pipeline, PatchProgram program, string settingsPath)
    {
        var field = program.SettingsField
                    ?? throw new InvalidOperationException("The program has settings but no single public static Lazy<TSettings> field to receive them.");
        var settingsType = field.FieldType.GetGenericArguments()[0];
        var arguments = new object?[] { settingsType.Name, settingsPath, null, false };
        typeof(SynthesisPipeline).GetMethod(nameof(SynthesisPipeline.SetAutogeneratedSettings))!
            .MakeGenericMethod(settingsType)
            .Invoke(pipeline, arguments);
        field.SetValue(null, arguments[2]);
    }

    /// <summary>
    /// Data files the host shares in <see cref="Start"/> whenever they exist (see SynthesisInputs):
    /// plugins, archives and strings. Mutagen probes for missing ones (e.g. implicit masters); asking
    /// the host about them would only add noise.
    /// </summary>
    private static bool SharedUpFront(string dataRelativePath) =>
        Path.GetExtension(dataRelativePath).ToLowerInvariant() is ".esm" or ".esp" or ".esl" or ".bsa" or ".ba2"
        || dataRelativePath.StartsWith(@"Strings", StringComparison.OrdinalIgnoreCase);

    /// <summary>The run's paths, parsed exactly as Synthesis parses them.</summary>
    private static RunSynthesisMutagenPatcher ParseRun(IReadOnlyList<string> arguments) =>
        new Parser(s => s.IgnoreUnknownArguments = true)
            .ParseArguments<RunSynthesisMutagenPatcher>(arguments)
            .MapResult(a => a, _ => throw new ArgumentException("Arguments are not a Synthesis patcher run."));
}
