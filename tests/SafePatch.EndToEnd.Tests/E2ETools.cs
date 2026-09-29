using System.Diagnostics;
using System.Text.Json;

namespace SafePatch.EndToEnd.Tests;

/// <summary>Paths to the pinned tools, written next to the test assembly by the build.</summary>
public sealed record E2ETools(string ModOrganizer, string SynthesisCli)
{
    private static readonly Lazy<E2ETools?> Loaded = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "e2e-tools.json");
        if (!File.Exists(path)) return null;
        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return new E2ETools(json.GetProperty("modOrganizer").GetString()!, json.GetProperty("synthesisCli").GetString()!);
    });

    /// <summary>The tools, or skips the test if the build did not fetch them.</summary>
    public static E2ETools Require()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "End-to-end tests need Windows.");
        Assert.SkipWhen(Loaded.Value is null, "e2e-tools.json missing: the build fetches it unless -p:SkipE2ETools=true.");
        return Loaded.Value!;
    }
}

public sealed record ProcessResult(int ExitCode, string Output)
{
    public override string ToString() => $"exit code {ExitCode}\n{Output}";
}

public static class Processes
{
    public static ProcessResult Run(string exe, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        // Synthesis reads `dotnet build` output to the end. Reused MSBuild nodes and the compiler
        // server inherit that output pipe and outlive the build, so the read would wait until they
        // idle out (many minutes). Keep every build these tools start self-contained.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["UseSharedCompilation"] = "false";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(info)!;
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} did not finish.\n{output}");
        }
        process.WaitForExit();
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }
}
