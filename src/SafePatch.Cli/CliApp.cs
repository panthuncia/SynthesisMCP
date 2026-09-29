using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda;
using SafePatch.Authoring;
using SafePatch.Host;

namespace SafePatch.Cli;

/// <summary>
/// The <c>safepatch</c> command line: each command maps to one <see cref="AuthoringService"/> operation
/// and writes its result as JSON. Exit codes: 0 done, 1 rejected or invalid, 2 usage error.
/// </summary>
public static class CliApp
{
    public const string Usage = """
        safepatch <command> [arguments] [options]

        Load order (all need --data <Data folder> --plugins <plugins.txt>, optional --release SkyrimSE):
          load-order                      the plugins, in order
          record <FormKey|EditorID>       the winning version of a record
          chain <FormKey|EditorID>        every version, and what each plugin changed
          query <Type> [--editor-id <text>] [--limit <n>]
                                          winning records of a type (Mutagen class name, e.g. LeveledItem)
          refs <FormKey|EditorID> [--limit <n>]
                                          winning records that link to a record
          test <program.cs> <scope>       run in the sandbox against the load order; nothing is written to it

        Programs:
          validate <program.cs> [--settings <settings.cs>]
          package <program.cs> <scope> --name <Name> --out <empty folder>
                  [--description <text>] [--requires <a.esp,b.esp>] [--runtime-project <SafePatch.Synthesis.csproj>]

        <scope>: --writable <Type.Field,...> [--creatable <Type,...>] [--removable <Type,...>]
                 [--assets <glob,...>] [--max-records <n>] [--settings <settings.cs>] [--settings-json <values.json>]
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, IWorkerLauncher? launcher = null)
    {
        CommandLine command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine(Usage);
            return 2;
        }

        try
        {
            var (result, ok) = Execute(command, launcher);
            output.WriteLine(JsonSerializer.Serialize(result, Json));
            return ok ? 0 : 1;
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine(Usage);
            return 2;
        }
        catch (Exception e) when (e is SafePatchException or IOException)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }

    private static readonly HashSet<string> Commands = ["load-order", "record", "chain", "query", "refs", "test", "validate", "package"];

    private static (object Result, bool Ok) Execute(CommandLine command, IWorkerLauncher? launcher)
    {
        if (!Commands.Contains(command.Name)) throw new ArgumentException($"Unknown command '{command.Name}'.");
        switch (command.Name)
        {
            case "validate":
            {
                var result = AuthoringService.Validate(ReadFile(command.Positional(0)), command.Option("settings") is { } s ? ReadFile(s) : null);
                return (result, result.Success);
            }
            case "package":
            {
                var request = new PackageRequest(command.Required("name"), ReadFile(command.Positional(0)), Scope(command), command.Required("out"),
                    Settings(command), command.Option("description"), command.List("requires"), command.Option("runtime-project"));
                var result = AuthoringService.Package(request);
                return (result, result.Success);
            }
        }

        using var snapshot = LoadOrderSnapshot.Open(command.Required("data"), command.Required("plugins"),
            command.Option("release") is { } release ? Enum.Parse<GameRelease>(release, ignoreCase: true) : GameRelease.SkyrimSE);
        var service = new AuthoringService(snapshot, launcher);
        return command.Name switch
        {
            "load-order" => (service.GetLoadOrder(), true),
            "record" => (service.GetRecord(command.Positional(0)), true),
            "chain" => (service.GetOverrideChain(command.Positional(0)), true),
            "query" => (service.QueryRecords(command.Positional(0), command.Option("editor-id"), command.Int("limit", 100)), true),
            "refs" => (service.FindReferences(command.Positional(0), command.Int("limit", 100)), true),
            "test" => Test(service, command),
            _ => throw new ArgumentException($"Unknown command '{command.Name}'."),
        };
    }

    private static (object, bool) Test(AuthoringService service, CommandLine command)
    {
        var result = service.TestPatch(ReadFile(command.Positional(0)), Scope(command), Settings(command));
        return (result, result.Accepted);
    }

    private static PatchScope Scope(CommandLine command) => new(
        command.List("writable") ?? [], command.List("creatable"), command.List("removable"), command.List("assets"), command.Int("max-records", 10_000));

    private static SettingsInput? Settings(CommandLine command) => command.Option("settings") is { } source
        ? new SettingsInput(ReadFile(source), command.Option("settings-json") is { } json ? ReadFile(json) : null)
        : null;

    private static string ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new ArgumentException($"File {path} does not exist.");
}

/// <summary>A command, its positional arguments and its <c>--name value</c> options.</summary>
internal sealed record CommandLine(string Name, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Options)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-')) throw new ArgumentException("Missing command.");
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (i + 1 >= args.Count) throw new ArgumentException($"Option --{name} needs a value.");
            if (!options.TryAdd(name, args[++i])) throw new ArgumentException($"Option --{name} is given twice.");
        }
        return new CommandLine(args[0], positional, options);
    }

    public string Positional(int index) =>
        index < Arguments.Count ? Arguments[index] : throw new ArgumentException($"'{Name}' needs {index + 1} argument(s).");

    public string? Option(string name) => Options.GetValueOrDefault(name);

    public string Required(string name) => Option(name) ?? throw new ArgumentException($"'{Name}' needs --{name}.");

    public IReadOnlyList<string>? List(string name) =>
        Option(name)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public int Int(string name, int fallback) => Option(name) is not { } text ? fallback
        : int.TryParse(text, out var value) && value > 0 ? value : throw new ArgumentException($"--{name} must be a positive number.");
}
