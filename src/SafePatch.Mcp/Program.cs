using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutagen.Bethesda;
using SafePatch.Authoring;
using SafePatch.Mcp;

// safepatch-mcp [--data <Data folder> --plugins <plugins.txt> [--release SkyrimSE]]
// Speaks MCP over standard input and output; logs go to standard error.
string? Option(string name) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();

var data = Option("data");
var plugins = Option("plugins");
if ((data is null) != (plugins is null))
{
    Console.Error.WriteLine("Pass both --data and --plugins, or neither (then only validate_patch and package_synthesis_patcher work).");
    return 2;
}
var release = Option("release") is { } name ? Enum.Parse<GameRelease>(name, ignoreCase: true) : GameRelease.SkyrimSE;

var builder = Host.CreateApplicationBuilder([]);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(_ => new Workspace(data is null ? null : LoadOrderSnapshot.Open(data, plugins!, release)));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SafePatchTools>();
await builder.Build().RunAsync();
return 0;
