using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutagen.Bethesda;
using SafePatch.Authoring;
using SafePatch.Authoring.Sources;
using SafePatch.Mcp;

// safepatch-mcp [--data <Data folder> --plugins <plugins.txt> | --mo2 <MO2 instance folder> [--profile <name>]] [--release SkyrimSE]
//               [--worker-memory <MiB>]
// Speaks MCP over standard input and output; logs go to standard error.
string? Option(string name) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();

var data = Option("data");
var plugins = Option("plugins");
var mo2 = Option("mo2");
if ((data is null) != (plugins is null) || (mo2 is not null && data is not null))
{
    Console.Error.WriteLine("Pass --data and --plugins, or --mo2 [--profile], or none (then only validate_patch and package_synthesis_patcher work).");
    return 2;
}
GameRelease? release = Option("release") is { } name ? Enum.Parse<GameRelease>(name, ignoreCase: true) : null;
LoadOrderSource? source = mo2 is not null ? new Mo2ProfileSource(mo2, Option("profile"), release)
    : data is not null ? new DataFolderSource(data, plugins!, release ?? GameRelease.SkyrimSE)
    : null;

var builder = Host.CreateApplicationBuilder([]);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
ulong? workerMemory;
try
{
    workerMemory = AuthoringService.WorkerMemory(Option("worker-memory"));
}
catch (SafePatch.Host.SafePatchException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
builder.Services.AddSingleton(_ => new AuthoringSession(source is null ? null : LoadOrderSnapshot.Open(source), workerMemoryBytes: workerMemory));
builder.Services.AddMcpServer(o => o.ServerInstructions = SafePatchTools.Instructions)
    .WithStdioServerTransport()
    .WithTools<SafePatchTools>()
    .WithResources<SafePatchResources>();
await builder.Build().RunAsync();
return 0;
