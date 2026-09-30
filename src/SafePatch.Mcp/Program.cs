using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutagen.Bethesda;
using SafePatch.Authoring;
using SafePatch.Authoring.Sources;
using SafePatch.Mcp;

// safepatch-mcp [--data <Data folder> --plugins <plugins.txt> | --mo2 <MO2 instance folder> [--profile <name>] [--mo2-exe <exe>] [--no-vfs]]
//               [--release SkyrimSE] [--worker-memory <MiB>]
// Speaks MCP over standard input and output; logs go to standard error. With --mo2 it runs inside MO2's virtual file
// system: it restarts itself through MO2 and relays its standard streams to that copy (Mo2Relay).
string? Option(string name) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();

Mo2Relay.Endpoint? relay = null;
if (OperatingSystem.IsWindows())
{
    if (Mo2Relay.Needed(args))
    {
        try
        {
            return Mo2Relay.Run(Option("mo2")!, Option("profile"), Option("mo2-exe"), args,
                Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());
        }
        catch (SafePatch.Host.SafePatchException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
    if (Option(Mo2Relay.PipeOption[2..]) is { } pipe)
    {
        relay = Mo2Relay.Connect(pipe);
        Console.SetError(new StreamWriter(relay.Error, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
    }
}

var data = Option("data");
var plugins = Option("plugins");
var mo2 = Option("mo2");
if ((data is null) != (plugins is null) || (mo2 is not null && data is not null))
{
    Console.Error.WriteLine("Pass --data and --plugins, or --mo2 [--profile], or none (then only validate_patch and package_synthesis_patcher work).");
    return Done(2);
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
    return Done(2);
}
builder.Services.AddSingleton(_ => new AuthoringSession(source is null ? null : LoadOrderSnapshot.Open(source), workerMemoryBytes: workerMemory));
var server = builder.Services.AddMcpServer(o => o.ServerInstructions = SafePatchTools.Instructions);
if (relay is not null) server.WithStreamServerTransport(relay.Input, relay.Output);
else server.WithStdioServerTransport();
server.WithTools<SafePatchTools>().WithResources<SafePatchResources>();
await builder.Build().RunAsync();
return Done(0);

// The copy MO2 started reports its exit code through the relay.
int Done(int code)
{
    relay?.Exit(code);
    relay?.Dispose();
    return code;
}
